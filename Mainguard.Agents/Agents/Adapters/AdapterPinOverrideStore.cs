using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mainguard.Git;

namespace Mainguard.Agents.Agents.Adapters;

/// <summary>A previously effective pin, kept so a user-applied CLI update can be reverted.</summary>
public sealed record AdapterPinSnapshot(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("payloadUrl")] string PayloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256);

/// <summary>
/// A user-applied pin override for one adapter: the version/payload/sha256 that replaces the bundled
/// manifest's pin when the user accepted an update (<see cref="AgentCliUpdateService"/>). The pinning
/// DISCIPLINE is unchanged — an override is still a concrete version with a sha256 computed from the
/// exact bytes that will be installed; only the pin's ORIGIN moves from the shipped manifest to the
/// user's explicit choice. <see cref="Previous"/> is the one-step history the settings "Revert"
/// restores when a new CLI version breaks the app.
/// </summary>
public sealed record AdapterPinOverride(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("payloadUrl")] string PayloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("previous")] AdapterPinSnapshot? Previous = null)
{
    /// <summary>The manifest spec with this override applied: version, payload, hash, and the
    /// version-matched probe substring all move together (a probe still expecting the old version
    /// would fail every install as a VersionMismatch).</summary>
    public AdapterSpec Apply(AdapterSpec spec) => spec with
    {
        Version = Version,
        PayloadUrl = PayloadUrl,
        Sha256 = Sha256,
        HealthProbe = spec.HealthProbe is null
            ? null
            : spec.HealthProbe with { ExpectedVersionSubstring = Version },
    };
}

/// <summary>Persistence for the per-adapter pin overrides. File-backed in production; injectable
/// for tests (the update/revert flows are exercised with an in-memory fake).</summary>
public interface IAdapterPinOverrideStore
{
    AdapterPinOverride? TryGet(string adapterId);
    void Set(string adapterId, AdapterPinOverride pin);
    void Remove(string adapterId);
}

/// <summary>
/// The file-backed override store: one JSON dictionary at
/// <c>%LocalAppData%\Mainguard\adapters\pin-overrides.json</c>. Set validates like the manifest
/// parser (a concrete pinned version, a 64-hex sha256, an HTTPS payload URL) so a corrupt or
/// hand-edited entry can never weaken what <see cref="AdapterChannel.EnsureAsync"/> installs; a
/// corrupt FILE reads as "no overrides" (the bundled pins simply apply).
/// </summary>
public sealed class FileAdapterPinOverrideStore : IAdapterPinOverrideStore
{
    private readonly string _path;

    public FileAdapterPinOverrideStore(string? path = null)
    {
        // MainguardPaths, not GetFolderPath: same service-context relative-path hazard as the
        // manifest cache next to this file.
        _path = path ?? Path.Combine(MainguardPaths.DataRoot(), "adapters", "pin-overrides.json");
    }

    public AdapterPinOverride? TryGet(string adapterId)
        => ReadAll().TryGetValue(adapterId, out var pin) ? pin : null;

    public void Set(string adapterId, AdapterPinOverride pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterId);
        Validate(pin);
        var all = ReadAll();
        all[adapterId] = pin;
        WriteAll(all);
    }

    public void Remove(string adapterId)
    {
        var all = ReadAll();
        if (all.Remove(adapterId))
        {
            WriteAll(all);
        }
    }

    private static void Validate(AdapterPinOverride pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (!AdapterManifest.IsPinnedVersion(pin.Version))
            throw new ArgumentException($"Override version '{pin.Version}' is not pinned to a concrete release.");
        if (pin.Sha256 is not { Length: 64 })
            throw new ArgumentException("Override sha256 must be 64 hex chars.");
        if (!pin.PayloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Override payloadUrl must be HTTPS.");
    }

    private Dictionary<string, AdapterPinOverride> ReadAll()
    {
        try
        {
            if (!File.Exists(_path))
                return new Dictionary<string, AdapterPinOverride>(StringComparer.Ordinal);
            return JsonSerializer.Deserialize<Dictionary<string, AdapterPinOverride>>(File.ReadAllText(_path))
                ?? new Dictionary<string, AdapterPinOverride>(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // A corrupt override file must never brick installs — the bundled pins simply apply.
            return new Dictionary<string, AdapterPinOverride>(StringComparer.Ordinal);
        }
    }

    private void WriteAll(Dictionary<string, AdapterPinOverride> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }
}
