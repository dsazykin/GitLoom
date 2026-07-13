using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace GitLoom.Core.Review;

/// <summary>
/// The <b>offline</b> OSV (Open Source Vulnerabilities) snapshot the review path consults for CVE hits
/// (P2-11 §3.6). It is a shipped, embedded resource — a review-time <b>network call is a rejection
/// trigger</b>. The snapshot is refreshed out-of-band; <see cref="Lookup"/> only ever reads this local
/// copy. Tests can supply their own in-memory snapshot via <see cref="FromEntries"/>.
/// </summary>
public sealed class OsvSnapshot
{
    // name (lower-case) → set of versions → advisory ids.
    private readonly Dictionary<string, Dictionary<string, List<string>>> _byName;

    private OsvSnapshot(Dictionary<string, Dictionary<string, List<string>>> byName) => _byName = byName;

    private static readonly Lazy<OsvSnapshot> _default = new(LoadEmbedded);

    /// <summary>The shipped snapshot (embedded resource). Never performs IO beyond reading itself once.</summary>
    public static OsvSnapshot Default => _default.Value;

    /// <summary>The snapshot's stated capture date (informational; surfaced so "offline" is honest).</summary>
    public string SnapshotDate { get; private set; } = "";

    /// <summary>The advisory ids affecting <paramref name="name"/> at exactly <paramref name="version"/>.</summary>
    public IReadOnlyList<string> Lookup(string name, string? version)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
        {
            return Array.Empty<string>();
        }

        if (_byName.TryGetValue(name.Trim().ToLowerInvariant(), out var byVersion)
            && byVersion.TryGetValue(version.Trim(), out var ids))
        {
            return ids;
        }

        return Array.Empty<string>();
    }

    /// <summary>Builds an in-memory snapshot (for tests) from (id, name, versions) advisory tuples.</summary>
    public static OsvSnapshot FromEntries(IEnumerable<(string Id, string Name, IReadOnlyList<string> Versions)> advisories)
    {
        var byName = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        foreach (var (id, name, versions) in advisories)
        {
            Add(byName, id, name, versions);
        }

        return new OsvSnapshot(byName);
    }

    private static OsvSnapshot LoadEmbedded()
    {
        var byName = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        var snapshot = new OsvSnapshot(byName);

        try
        {
            var asm = typeof(OsvSnapshot).Assembly;
            var resourceName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("OsvSnapshot.json", StringComparison.Ordinal));
            if (resourceName is null)
            {
                return snapshot;
            }

            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return snapshot;
            }

            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;

            if (root.TryGetProperty("snapshotDate", out var date) && date.ValueKind == JsonValueKind.String)
            {
                snapshot.SnapshotDate = date.GetString() ?? "";
            }

            if (root.TryGetProperty("advisories", out var advisories) && advisories.ValueKind == JsonValueKind.Array)
            {
                foreach (var adv in advisories.EnumerateArray())
                {
                    var id = adv.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var name = adv.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var versions = new List<string>();
                    if (adv.TryGetProperty("versions", out var vArr) && vArr.ValueKind == JsonValueKind.Array)
                    {
                        versions.AddRange(vArr.EnumerateArray()
                            .Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()!)
                            .Where(s => !string.IsNullOrWhiteSpace(s)));
                    }

                    Add(byName, id!, name!, versions);
                }
            }
        }
        catch (JsonException)
        {
            // A malformed snapshot must not crash review — it degrades to "no known CVEs".
        }

        return snapshot;
    }

    private static void Add(
        Dictionary<string, Dictionary<string, List<string>>> byName,
        string id,
        string name,
        IReadOnlyList<string> versions)
    {
        var key = name.Trim().ToLowerInvariant();
        if (!byName.TryGetValue(key, out var byVersion))
        {
            byVersion = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            byName[key] = byVersion;
        }

        foreach (var version in versions)
        {
            var v = version.Trim();
            if (!byVersion.TryGetValue(v, out var ids))
            {
                ids = new List<string>();
                byVersion[v] = ids;
            }

            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }
    }
}
