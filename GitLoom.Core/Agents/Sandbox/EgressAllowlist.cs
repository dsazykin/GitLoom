using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using GitLoom.Core.Audit;
using GitLoom.Core.Security;

namespace GitLoom.Core.Agents.Sandbox;

/// <summary>What an allowlist entry is for — drives the UI grouping and the A6 git-host warning.</summary>
public enum EgressEntryKind
{
    ModelApi,
    PackageRegistry,
    GitHost,
    Custom,
}

/// <summary>
/// One egress-allowlist entry: a friendly <paramref name="Name"/> and a <paramref name="HostPattern"/>
/// (a hostname the proxy + pinned DNS will answer). <see cref="DefeatsA6"/> flags an entry that
/// re-opens a direct route to a git host — the A6 structural control is the git host's <b>absence</b>
/// from the agent allowlist, so a user-added git-host entry is surfaced as defeating it.
/// </summary>
public sealed record EgressAllowlistEntry(string Name, string HostPattern, EgressEntryKind Kind)
{
    /// <summary>True iff this entry re-opens a direct git-host route (A6 defeated).</summary>
    public bool DefeatsA6 => Kind == EgressEntryKind.GitHost || LooksLikeGitHost(HostPattern);

    /// <summary>Recognises a hostname as a git host (known providers or a "git"-prefixed host).</summary>
    public static bool LooksLikeGitHost(string hostPattern)
    {
        if (string.IsNullOrWhiteSpace(hostPattern)) return false;
        var host = hostPattern.Trim().ToLowerInvariant();

        var (_, kind) = GitHostDetector.Detect("https://" + host + "/owner/repo.git");
        if (kind != Core.Models.HostKind.Unknown) return true;

        // Self-hosted / enterprise git hosts commonly carry a "git." label.
        return host.StartsWith("git.", StringComparison.Ordinal)
            || host.StartsWith("git-", StringComparison.Ordinal)
            || host is "github.com" or "gitlab.com" or "bitbucket.org"
            || host.EndsWith(".github.com", StringComparison.Ordinal)
            || host.EndsWith(".gitlab.com", StringComparison.Ordinal);
    }
}

/// <summary>
/// The default-deny egress allowlist (P2-07 §3.3): the model APIs and package registries an agent
/// may reach through the proxy. It is user-visible and editable; every add/remove emits an
/// <c>allowlist_changed</c> audit event (feeds P2-17 transparency / P2-15 chaining). The provisioned
/// repo's git host is <b>deliberately not a default</b> (A6) — git-sourced installs go through the
/// daemon read-only git proxy, never the agent's own egress.
/// </summary>
public sealed class EgressAllowlist
{
    private readonly List<EgressAllowlistEntry> _entries;
    private readonly IAuditLog _audit;

    public const string ChangeEventType = "allowlist_changed";

    public EgressAllowlist(IEnumerable<EgressAllowlistEntry> entries, IAuditLog audit)
    {
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _entries = entries?.ToList() ?? throw new ArgumentNullException(nameof(entries));
    }

    /// <summary>The current entries (snapshot).</summary>
    public IReadOnlyList<EgressAllowlistEntry> Entries => _entries.ToArray();

    /// <summary>True iff any entry re-opens a git-host route (A6 defeated) — surfaced by the UI.</summary>
    public bool HasGitHostEntry => _entries.Any(e => e.DefeatsA6);

    /// <summary>Does the allowlist permit <paramref name="host"/> (exact or suffix-wildcard match)?</summary>
    public bool Allows(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().ToLowerInvariant();
        return _entries.Any(e => HostMatches(e.HostPattern, h));
    }

    /// <summary>Adds an entry and emits the change event; a duplicate (by host) is a no-op.</summary>
    public void Add(EgressAllowlistEntry entry, string who)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (_entries.Any(e => string.Equals(e.HostPattern, entry.HostPattern, StringComparison.OrdinalIgnoreCase)))
            return;
        _entries.Add(entry);
        EmitChange("add", entry, who);
    }

    /// <summary>Removes the entry with <paramref name="hostPattern"/> and emits the change event.</summary>
    public bool Remove(string hostPattern, string who)
    {
        var existing = _entries.FirstOrDefault(e => string.Equals(e.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return false;
        _entries.Remove(existing);
        EmitChange("remove", existing, who);
        return true;
    }

    private void EmitChange(string action, EgressAllowlistEntry entry, string who)
    {
        _audit.Append(new AuditEvent(ChangeEventType, new Dictionary<string, string>
        {
            ["who"] = who,
            ["when"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["entry"] = entry.HostPattern,
            ["name"] = entry.Name,
            ["kind"] = entry.Kind.ToString(),
            ["action"] = action,
            ["defeats_a6"] = entry.DefeatsA6 ? "true" : "false",
        }));
    }

    private static bool HostMatches(string pattern, string host)
    {
        var p = pattern.Trim().ToLowerInvariant();
        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = p[1..]; // ".example.com"
            return host.EndsWith(suffix, StringComparison.Ordinal) || host == p[2..];
        }
        return host == p;
    }

    /// <summary>
    /// The default entries: model APIs + package registries only. <b>No git host</b> (A6). This exact
    /// set is pinned by <c>EgressAllowlistTests.Defaults_ContainNoGitHostEntry</c>.
    /// </summary>
    public static IReadOnlyList<EgressAllowlistEntry> DefaultEntries { get; } = new[]
    {
        new EgressAllowlistEntry("Anthropic API", "api.anthropic.com", EgressEntryKind.ModelApi),
        new EgressAllowlistEntry("OpenAI API", "api.openai.com", EgressEntryKind.ModelApi),
        new EgressAllowlistEntry("npm registry", "registry.npmjs.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("PyPI", "pypi.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("PyPI files", "files.pythonhosted.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("NuGet API", "api.nuget.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("NuGet gallery", "www.nuget.org", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("crates.io", "crates.io", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("crates.io downloads", "static.crates.io", EgressEntryKind.PackageRegistry),
        new EgressAllowlistEntry("Go module proxy", "proxy.golang.org", EgressEntryKind.PackageRegistry),
    };

    /// <summary>An allowlist seeded with <see cref="DefaultEntries"/>.</summary>
    public static EgressAllowlist WithDefaults(IAuditLog audit) => new(DefaultEntries, audit);

    /// <summary>Serialises the current entries (per-repo persistence).</summary>
    public string ToPersistedForm() =>
        JsonSerializer.Serialize(_entries.Select(e => new PersistedEntry(e.Name, e.HostPattern, e.Kind.ToString())).ToList());

    /// <summary>Rehydrates an allowlist from <see cref="ToPersistedForm"/> output (round-trips).</summary>
    public static EgressAllowlist FromPersistedForm(string json, IAuditLog audit)
    {
        var persisted = JsonSerializer.Deserialize<List<PersistedEntry>>(json) ?? new List<PersistedEntry>();
        var entries = persisted.Select(p =>
            new EgressAllowlistEntry(p.Name, p.HostPattern, Enum.Parse<EgressEntryKind>(p.Kind)));
        return new EgressAllowlist(entries, audit);
    }

    private sealed record PersistedEntry(string Name, string HostPattern, string Kind);
}
