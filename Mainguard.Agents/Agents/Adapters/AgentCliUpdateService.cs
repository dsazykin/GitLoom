using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>One available agent-CLI update: the effective installed pin vs the registry's latest.</summary>
public sealed record AgentCliUpdate(string Id, string DisplayName, string InstalledVersion, string LatestVersion);

/// <summary>
/// The Mainguard-managed CLI updater. The in-CLI self-updaters are disabled in every jail (the
/// adapters mount is read-only and versions are pinned), so THIS is how a CLI moves forward: check
/// the npm registry for a newer release, and only on the user's explicit accept, download the exact
/// tarball, compute its sha256, store it as a pin OVERRIDE (with the current pin as the one-step
/// revert history), and run the same hash-verified <see cref="AdapterChannel.EnsureAsync"/> install
/// path the pinned channel always uses. Nothing here weakens the pin discipline: an update is a new
/// concrete pin chosen by the user, never a floating <c>@latest</c>.
///
/// <para><b>Revert</b> restores the previous pin the same way — the settings window offers it so a
/// CLI release that breaks the app is a one-click rollback, not a re-setup.</para>
/// </summary>
public sealed class AgentCliUpdateService
{
    private readonly AdapterChannel _channel;
    private readonly IAdapterPinOverrideStore _pins;
    private readonly HttpClient _http;

    /// <param name="handler">Injected transport for offline tests; null → a real handler.</param>
    public AgentCliUpdateService(
        AdapterChannel channel, IAdapterPinOverrideStore pins, HttpMessageHandler? handler = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _pins = pins ?? throw new ArgumentNullException(nameof(pins));
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true);
    }

    /// <summary>The default composition: the bundled channel + the file-backed override store,
    /// installing into the MainguardEnv VM (mirrors <see cref="AgentCliInstaller.CreateDefault"/>).</summary>
    public static AgentCliUpdateService CreateDefault(IWslRunner wsl)
    {
        var host = new WslAdapterInstallHost(wsl);
        var pins = new FileAdapterPinOverrideStore();
        var channel = new AdapterChannel(
            new BundledAdapterChannelSource(), host, new FileAdapterManifestCache(), pins: pins);
        return new AgentCliUpdateService(channel, pins);
    }

    /// <summary>
    /// The npm package name a registry tarball URL pins (e.g.
    /// <c>https://registry.npmjs.org/@anthropic-ai/claude-code/-/claude-code-2.1.210.tgz</c> →
    /// <c>@anthropic-ai/claude-code</c>). Null for any non-npmjs payload — those CLIs simply have
    /// no update channel and never appear in the check.
    /// </summary>
    internal static string? TryParseNpmPackage(string? payloadUrl)
    {
        if (payloadUrl is null || !Uri.TryCreate(payloadUrl, UriKind.Absolute, out var url))
            return null;
        if (!string.Equals(url.Host, "registry.npmjs.org", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = Uri.UnescapeDataString(url.AbsolutePath).TrimStart('/');
        var separator = path.IndexOf("/-/", StringComparison.Ordinal);
        return separator > 0 ? path[..separator] : null;
    }

    /// <summary>
    /// Checks every npm-sourced CLI in the channel for a newer release. Per-CLI failures (registry
    /// unreachable, junk metadata) skip that CLI and never fail the sweep — an update check must be
    /// harmless at app launch. Returns only real, concrete-version upgrades.
    /// </summary>
    public async Task<IReadOnlyList<AgentCliUpdate>> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var updates = new List<AgentCliUpdate>();
        foreach (var raw in manifest.Adapters)
        {
            ct.ThrowIfCancellationRequested();
            var spec = _channel.EffectiveSpec(raw);
            var package = TryParseNpmPackage(spec.PayloadUrl);
            if (package is null)
                continue;

            string? latest;
            try
            {
                latest = await FetchLatestVersionAsync(package, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                continue; // this CLI's registry lookup failed — keep checking the rest
            }

            if (latest is null
                || string.Equals(latest, spec.Version, StringComparison.Ordinal)
                || !AdapterManifest.IsPinnedVersion(latest))
            {
                continue;
            }

            updates.Add(new AgentCliUpdate(spec.Id, spec.DisplayName, spec.Version, latest));
        }

        return updates;
    }

    /// <summary>
    /// Applies a user-accepted update: fetch the exact registry tarball for
    /// <paramref name="version"/>, sha256 it (the new pin covers precisely these advertised bytes),
    /// store the override with the current pin as revert history, and install through the channel's
    /// normal verify→install→probe path. A failed install restores the prior override state so a
    /// broken update can never wedge the CLI's pin.
    /// </summary>
    public async Task ApplyUpdateAsync(string adapterId, string version, CancellationToken ct = default)
    {
        var current = await EffectiveSpecAsync(adapterId, ct).ConfigureAwait(false);
        var package = TryParseNpmPackage(current.PayloadUrl)
            ?? throw new AdapterChannelException(AdapterChannelError.UnknownAdapter,
                $"'{adapterId}' is not an npm-channel CLI — it has no update path.");

        var (tarballUrl, bytes) = await FetchTarballAsync(package, version, ct).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var before = _pins.TryGet(adapterId);
        _pins.Set(adapterId, new AdapterPinOverride(
            version, tarballUrl, sha256,
            new AdapterPinSnapshot(current.Version, current.PayloadUrl!, current.Sha256)));
        try
        {
            await _channel.EnsureAsync(adapterId, ct).ConfigureAwait(false);
        }
        catch
        {
            RestorePin(adapterId, before);
            throw;
        }
    }

    /// <summary>The version "Revert" would restore for <paramref name="adapterId"/>, or null when
    /// no accepted update left a previous pin behind.</summary>
    public string? PreviousVersion(string adapterId) => _pins.TryGet(adapterId)?.Previous?.Version;

    /// <summary>
    /// Reverts an accepted update to the pin it replaced (the settings escape hatch for a CLI
    /// release that breaks the app). Reverting to the bundled pin simply removes the override;
    /// reverting to an earlier accepted update re-pins it. Installs through the same verified path.
    /// </summary>
    public async Task RevertAsync(string adapterId, CancellationToken ct = default)
    {
        var before = _pins.TryGet(adapterId);
        if (before?.Previous is not { } previous)
            throw new InvalidOperationException($"'{adapterId}' has no previous version to revert to.");

        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var bundled = manifest.Adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.Ordinal));
        if (bundled is not null
            && string.Equals(previous.Version, bundled.Version, StringComparison.Ordinal)
            && string.Equals(previous.Sha256, bundled.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            _pins.Remove(adapterId); // back to the shipped truth — no override needed
        }
        else
        {
            _pins.Set(adapterId, new AdapterPinOverride(previous.Version, previous.PayloadUrl, previous.Sha256));
        }

        try
        {
            await _channel.EnsureAsync(adapterId, ct).ConfigureAwait(false);
        }
        catch
        {
            RestorePin(adapterId, before);
            throw;
        }
    }

    private void RestorePin(string adapterId, AdapterPinOverride? before)
    {
        try
        {
            if (before is null)
                _pins.Remove(adapterId);
            else
                _pins.Set(adapterId, before);
        }
        catch
        {
            // Restoring the pin is best-effort on an already-failing path.
        }
    }

    private async Task<AdapterSpec> EffectiveSpecAsync(string adapterId, CancellationToken ct)
    {
        var manifest = await _channel.LoadManifestAsync(ct).ConfigureAwait(false);
        var raw = manifest.Adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.Ordinal))
            ?? throw new AdapterChannelException(AdapterChannelError.UnknownAdapter,
                $"No adapter '{adapterId}' in the channel manifest.");
        return _channel.EffectiveSpec(raw);
    }

    private async Task<string?> FetchLatestVersionAsync(string package, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(
            new Uri($"https://registry.npmjs.org/{package}/latest"), ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    private async Task<(string Url, byte[] Bytes)> FetchTarballAsync(string package, string version, CancellationToken ct)
    {
        var json = await _http.GetStringAsync(
            new Uri($"https://registry.npmjs.org/{package}/{Uri.EscapeDataString(version)}"), ct).ConfigureAwait(false);
        string? tarball;
        using (var doc = JsonDocument.Parse(json))
        {
            tarball = doc.RootElement.TryGetProperty("dist", out var dist)
                && dist.TryGetProperty("tarball", out var url) ? url.GetString() : null;
        }

        // The registry names its own tarball URL; hold it to the same shape the manifest enforces.
        if (tarball is null
            || !Uri.TryCreate(tarball, UriKind.Absolute, out var tarballUri)
            || tarballUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(tarballUri.Host, "registry.npmjs.org", StringComparison.OrdinalIgnoreCase))
        {
            throw new AdapterChannelException(AdapterChannelError.UnknownAdapter,
                $"The npm registry did not name a usable tarball for {package}@{version}.");
        }

        var bytes = await _http.GetByteArrayAsync(tarballUri, ct).ConfigureAwait(false);
        return (tarball, bytes);
    }
}
