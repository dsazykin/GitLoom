using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.Core.Agents.Adapters;

/// <summary>Why an adapter install was refused/failed. Typed so callers react, never a bare throw.</summary>
public enum AdapterChannelError
{
    /// <summary>The manifest names no adapter with the requested id.</summary>
    UnknownAdapter,
    /// <summary>The fetched payload's SHA-256 did not match the manifest pin — install refused.</summary>
    HashMismatch,
    /// <summary>The in-VM install command exited non-zero.</summary>
    InstallFailed,
    /// <summary>The health probe exited non-zero (the CLI is not runnable).</summary>
    ProbeFailed,
    /// <summary>The health probe ran but did not report the pinned version (wrong version installed).</summary>
    VersionMismatch,
}

/// <summary>The typed refusal/failure of an adapter operation.</summary>
public sealed class AdapterChannelException : Exception
{
    public AdapterChannelError Error { get; }

    public AdapterChannelException(AdapterChannelError error, string message)
        : base(message) => Error = error;
}

/// <summary>Outcome of <see cref="AdapterChannel.EnsureAsync"/>.</summary>
public enum AdapterEnsureResult
{
    /// <summary>The pinned version was already installed and healthy — nothing was done (idempotent).</summary>
    AlreadyHealthy,
    /// <summary>The pinned version was fetched, verified, installed, shimmed, and probed green.</summary>
    Installed,
}

/// <summary>The GitLoom-owned channel: serves the manifest and the hash-pinned adapter payloads over
/// HTTPS. Behind an interface so the pin-survival simulation drives it with a fixture registry.</summary>
public interface IAdapterChannelSource
{
    Task<string> FetchManifestAsync(CancellationToken ct);
    Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct);
}

/// <summary>Result of one in-VM command.</summary>
public sealed record AdapterCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Runs adapter install/probe commands INSIDE the VM (never on the host). The real impl wraps
/// the P2-05 <see cref="IWslRunner"/> (<c>wsl -d GitLoomEnv --</c>); the fake drives the logic tests.</summary>
public interface IAdapterInstallHost
{
    Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct);
    Task WriteFileAsync(string path, string content, CancellationToken ct);
}

/// <summary>Persists the last-fetched manifest under appdata so installs work offline and refresh is
/// independent of app releases.</summary>
public interface IAdapterManifestCache
{
    string? Read();
    void Write(string manifestJson);
}

/// <summary>
/// The pinned adapter channel (P2-22 §J-5). Installs agent CLIs INSIDE the VM at the exact version the
/// manifest pins — never <c>@latest</c> — verifying a content-hash before install and a version-matched
/// health probe after. Because the install command and probe both carry the pinned version, a breaking
/// upstream release cannot change what is installed: pin survival is structural, and the simulation test
/// proves it. Refresh (re-fetching the manifest) is a separate, explicit call so app updates and adapter
/// updates move independently (perpetual-fallback licenses keep working — market v2 §5.3).
/// </summary>
public sealed class AdapterChannel
{
    private readonly IAdapterChannelSource _source;
    private readonly IAdapterInstallHost _host;
    private readonly IAdapterManifestCache _cache;

    public AdapterChannel(IAdapterChannelSource source, IAdapterInstallHost host, IAdapterManifestCache cache)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    /// <summary>Fetches and caches a fresh manifest from the channel (explicit — never implicit on
    /// install, so a breaking upstream is never auto-adopted). Returns the validated manifest.</summary>
    public async Task<AdapterManifest> RefreshAsync(CancellationToken ct = default)
    {
        var json = await _source.FetchManifestAsync(ct).ConfigureAwait(false);
        var manifest = AdapterManifest.Parse(json); // validates before we ever cache/act on it
        _cache.Write(json);
        return manifest;
    }

    /// <summary>Loads the manifest from cache, or refreshes once if the cache is empty.</summary>
    public async Task<AdapterManifest> LoadManifestAsync(CancellationToken ct = default)
    {
        var cached = _cache.Read();
        return cached is not null ? AdapterManifest.Parse(cached) : await RefreshAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures the pinned adapter <paramref name="adapterId"/> is installed and healthy in the VM. If
    /// the probe already reports the pinned version it is a no-op. Otherwise: fetch payload → verify
    /// SHA-256 against the pin (refuse on mismatch) → run install in VM → write config shims → probe;
    /// the probe must exit 0 AND report the pinned version substring.
    /// </summary>
    public async Task<AdapterEnsureResult> EnsureAsync(string adapterId, CancellationToken ct = default)
    {
        var manifest = await LoadManifestAsync(ct).ConfigureAwait(false);
        var spec = manifest.Adapters.FirstOrDefault(a => string.Equals(a.Id, adapterId, StringComparison.Ordinal))
            ?? throw new AdapterChannelException(AdapterChannelError.UnknownAdapter, $"No adapter '{adapterId}' in the channel manifest.");

        // Idempotent: a green probe at the pinned version means nothing to do.
        var pre = await _host.RunAsync(spec.HealthProbe!.Command, ct).ConfigureAwait(false);
        if (pre.Succeeded && pre.Stdout.Contains(spec.HealthProbe.ExpectedVersionSubstring, StringComparison.Ordinal))
            return AdapterEnsureResult.AlreadyHealthy;

        // Fetch the pinned payload and verify the content hash BEFORE running anything.
        var payload = await _source.FetchPayloadAsync(spec, ct).ConfigureAwait(false);
        var actual = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actual),
                System.Text.Encoding.ASCII.GetBytes(spec.Sha256.ToLowerInvariant())))
        {
            throw new AdapterChannelException(AdapterChannelError.HashMismatch,
                $"Adapter '{adapterId}' payload hash did not match the pinned sha256; install refused.");
        }

        // Install INSIDE the VM at the pinned version (installCmd carries the pin — never @latest).
        var install = await _host.RunAsync(spec.InstallCmd, ct).ConfigureAwait(false);
        if (!install.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed,
                $"Adapter '{adapterId}' install exited {install.ExitCode}: {install.Stderr}");

        // Config shims (e.g. non-interactive flags) so the CLI never blocks the daemon on a prompt.
        foreach (var shim in spec.ConfigShims ?? Array.Empty<ConfigShim>())
            await _host.WriteFileAsync(shim.Path, shim.Content, ct).ConfigureAwait(false);

        // Verify: exit 0 AND the pinned version is what actually landed.
        var post = await _host.RunAsync(spec.HealthProbe.Command, ct).ConfigureAwait(false);
        if (!post.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.ProbeFailed,
                $"Adapter '{adapterId}' health probe exited {post.ExitCode}.");
        if (!post.Stdout.Contains(spec.HealthProbe.ExpectedVersionSubstring, StringComparison.Ordinal))
            throw new AdapterChannelException(AdapterChannelError.VersionMismatch,
                $"Adapter '{adapterId}' probe reported the wrong version (pinned '{spec.Version}').");

        return AdapterEnsureResult.Installed;
    }
}

/// <summary>The real HTTPS channel source. Refuses a non-HTTPS channel URL (a plaintext channel would
/// let a MITM swap the manifest before the hash pin can protect the payloads).</summary>
public sealed class HttpsAdapterChannelSource : IAdapterChannelSource
{
    private readonly HttpClient _http;
    private readonly Uri _manifestUrl;
    private readonly Func<AdapterSpec, Uri> _payloadUrl;

    public HttpsAdapterChannelSource(Uri manifestUrl, Func<AdapterSpec, Uri> payloadUrl, HttpMessageHandler? handler = null)
    {
        if (manifestUrl.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("The adapter channel must be HTTPS.", nameof(manifestUrl));
        _manifestUrl = manifestUrl;
        _payloadUrl = payloadUrl;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
    }

    public async Task<string> FetchManifestAsync(CancellationToken ct) =>
        await _http.GetStringAsync(_manifestUrl, ct).ConfigureAwait(false);

    public async Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct)
    {
        var url = _payloadUrl(spec);
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Adapter payloads must be fetched over HTTPS.");
        return await _http.GetByteArrayAsync(url, ct).ConfigureAwait(false);
    }
}

/// <summary>The real in-VM install host: every command runs <c>wsl -d GitLoomEnv --</c> via the hardened
/// P2-05 runner. Config shims are written with <c>tee</c> over stdin (no shell redirection surface).</summary>
public sealed class WslAdapterInstallHost : IAdapterInstallHost
{
    private readonly IWslRunner _wsl;

    public WslAdapterInstallHost(IWslRunner wsl) => _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));

    public async Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.InDistro(command.ToArray()), stdin: null, ct).ConfigureAwait(false);
        return new AdapterCommandResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    public async Task WriteFileAsync(string path, string content, CancellationToken ct)
    {
        var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : ".";
        await _wsl.RunAsync(WslCommands.InDistro("mkdir", "-p", dir), stdin: null, ct).ConfigureAwait(false);
        var write = await _wsl.RunAsync(WslCommands.InDistro("tee", path), stdin: content, ct).ConfigureAwait(false);
        if (!write.Succeeded)
            throw new AdapterChannelException(AdapterChannelError.InstallFailed, $"Writing config shim '{path}' failed.");
    }
}

/// <summary>File-backed manifest cache under <c>%LocalAppData%\GitLoom\adapters\adapters.json</c>.</summary>
public sealed class FileAdapterManifestCache : IAdapterManifestCache
{
    private readonly string _path;

    public FileAdapterManifestCache(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitLoom", "adapters", "adapters.json");
    }

    public string? Read() => File.Exists(_path) ? File.ReadAllText(_path) : null;

    public void Write(string manifestJson)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, manifestJson);
    }
}
