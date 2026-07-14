using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLoom.Core.Agents.Adapters;

/// <summary>Why an adapter manifest was refused. Every rejection is typed — never a bare parse throw.</summary>
public enum AdapterManifestError
{
    /// <summary>The JSON was malformed or an unknown field was present (strict schema).</summary>
    Malformed,
    /// <summary>An adapter is missing a required field (id, version, sha256, install cmd, health probe).</summary>
    MissingField,
    /// <summary>A version is not pinned to a concrete release (e.g. <c>latest</c>, <c>@latest</c>, a range).</summary>
    UnpinnedVersion,
    /// <summary>The <c>sha256</c> pin is not 64 hex characters.</summary>
    BadHash,
    /// <summary>Two adapters share an id.</summary>
    DuplicateId,
}

/// <summary>The typed refusal of an adapter manifest.</summary>
public sealed class AdapterManifestException : Exception
{
    public AdapterManifestError Error { get; }

    public AdapterManifestException(AdapterManifestError error, string message)
        : base(message) => Error = error;
}

/// <summary>A file written into the VM before the health probe (e.g. a non-interactive config so the
/// pinned CLI never blocks on a prompt).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigShim(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);

/// <summary>The command that proves the pinned CLI is installed and at the right version.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HealthProbe(
    [property: JsonPropertyName("command")] IReadOnlyList<string> Command,
    [property: JsonPropertyName("expectedVersionSubstring")] string ExpectedVersionSubstring);

/// <summary>One pinned agent CLI: what to install, at exactly which version, verified by which probe.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdapterSpec(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("installCmd")] IReadOnlyList<string> InstallCmd,
    [property: JsonPropertyName("configShims")] IReadOnlyList<ConfigShim>? ConfigShims,
    [property: JsonPropertyName("healthProbe")] HealthProbe? HealthProbe);

/// <summary>The <c>adapters.json</c> channel manifest: the full set of pinned agent CLIs.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AdapterManifest(
    [property: JsonPropertyName("adapters")] IReadOnlyList<AdapterSpec> Adapters)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>
    /// Parses and schema-validates an <c>adapters.json</c>. Rejects malformed JSON, unknown fields,
    /// missing required fields, a non-64-hex <c>sha256</c>, duplicate ids, and — critically — any
    /// version that is not pinned to a concrete release (<c>latest</c>, <c>@latest</c>, or a range is
    /// refused; <c>@latest</c> installs are a rejection trigger, so they cannot even parse).
    /// </summary>
    public static AdapterManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        AdapterManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AdapterManifest>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new AdapterManifestException(AdapterManifestError.Malformed, $"Manifest JSON invalid: {ex.Message}");
        }

        if (manifest?.Adapters is null)
            throw new AdapterManifestException(AdapterManifestError.MissingField, "Manifest has no 'adapters' array.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in manifest.Adapters)
        {
            if (string.IsNullOrWhiteSpace(a.Id))
                throw new AdapterManifestException(AdapterManifestError.MissingField, "An adapter is missing 'id'.");
            if (!seen.Add(a.Id))
                throw new AdapterManifestException(AdapterManifestError.DuplicateId, $"Duplicate adapter id '{a.Id}'.");
            if (string.IsNullOrWhiteSpace(a.DisplayName))
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing 'displayName'.");
            if (string.IsNullOrWhiteSpace(a.Version))
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing 'version'.");
            if (!IsPinnedVersion(a.Version))
                throw new AdapterManifestException(AdapterManifestError.UnpinnedVersion,
                    $"Adapter '{a.Id}' version '{a.Version}' is not pinned to a concrete release.");
            if (a.InstallCmd is null || a.InstallCmd.Count == 0)
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing 'installCmd'.");
            if (a.InstallCmd.Any(ContainsUnpinnedToken))
                throw new AdapterManifestException(AdapterManifestError.UnpinnedVersion,
                    $"Adapter '{a.Id}' install command uses an unpinned tag (e.g. @latest).");
            if (!IsSha256(a.Sha256))
                throw new AdapterManifestException(AdapterManifestError.BadHash, $"Adapter '{a.Id}' sha256 must be 64 hex chars.");
            if (a.HealthProbe is null || a.HealthProbe.Command is null || a.HealthProbe.Command.Count == 0
                || string.IsNullOrWhiteSpace(a.HealthProbe.ExpectedVersionSubstring))
                throw new AdapterManifestException(AdapterManifestError.MissingField, $"Adapter '{a.Id}' is missing a valid 'healthProbe'.");
        }

        return manifest;
    }

    /// <summary>A version is pinned iff it is concrete: has a digit, and carries no range/wildcard/tag.</summary>
    public static bool IsPinnedVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim();
        if (v.Contains("latest", StringComparison.OrdinalIgnoreCase)) return false;
        if (v.Contains('*') || v.Contains('x') && !v.Any(char.IsDigit)) return false;
        // Range / wildcard operators disqualify a pin.
        if (v.StartsWith('^') || v.StartsWith('~') || v.StartsWith('>') || v.StartsWith('<') || v.StartsWith('='))
            return false;
        if (v.Contains("||", StringComparison.Ordinal) || v.Contains(" - ", StringComparison.Ordinal)) return false;
        return v.Any(char.IsDigit);
    }

    private static bool ContainsUnpinnedToken(string token) =>
        token.Contains("@latest", StringComparison.OrdinalIgnoreCase)
        || token.Equals("latest", StringComparison.OrdinalIgnoreCase)
        || token.EndsWith("@*", StringComparison.Ordinal)
        || token.EndsWith("@next", StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? hash) =>
        !string.IsNullOrEmpty(hash) && hash.Length == 64
        && hash.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
}
