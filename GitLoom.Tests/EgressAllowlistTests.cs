using System.Linq;
using GitLoom.Core.Agents.Sandbox;
using GitLoom.Core.Audit;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Pins the A6 structural control: the default egress allowlist contains NO git-host entry, and a
/// user-added git-host entry is flagged as defeating A6. Also covers persistence round-trips and the
/// change-audit events (P2-17 transparency feed).
/// </summary>
public class EgressAllowlistTests
{
    [Fact]
    public void Defaults_ContainNoGitHostEntry()
    {
        var allowlist = EgressAllowlist.WithDefaults(new InMemoryAuditLog());

        Assert.False(allowlist.HasGitHostEntry);
        Assert.DoesNotContain(allowlist.Entries, e => e.DefeatsA6);
        // Sanity: the model APIs + registries ARE present.
        Assert.Contains(allowlist.Entries, e => e.HostPattern == "api.anthropic.com");
        Assert.Contains(allowlist.Entries, e => e.HostPattern == "registry.npmjs.org");
        // And no known git host slipped in.
        foreach (var host in new[] { "github.com", "gitlab.com", "bitbucket.org", "dev.azure.com" })
            Assert.DoesNotContain(allowlist.Entries, e => e.HostPattern == host);
    }

    [Fact]
    public void Allows_MatchesDefaults_DeniesEverythingElse()
    {
        var allowlist = EgressAllowlist.WithDefaults(new InMemoryAuditLog());
        Assert.True(allowlist.Allows("api.anthropic.com"));
        Assert.False(allowlist.Allows("example.com"));
        Assert.False(allowlist.Allows("github.com"));
    }

    [Fact]
    public void AddThenRemove_RoundTrips_AndEmitsAuditEvents()
    {
        var audit = new InMemoryAuditLog();
        var allowlist = EgressAllowlist.WithDefaults(audit);
        var before = allowlist.Entries.Count;

        allowlist.Add(new EgressAllowlistEntry("Custom", "cdn.example.com", EgressEntryKind.Custom), who: "daniel");
        Assert.True(allowlist.Allows("cdn.example.com"));
        Assert.Equal(before + 1, allowlist.Entries.Count);

        allowlist.Remove("cdn.example.com", who: "daniel");
        Assert.False(allowlist.Allows("cdn.example.com"));
        Assert.Equal(before, allowlist.Entries.Count);

        var changes = audit.Read().Where(e => e.Type == EgressAllowlist.ChangeEventType).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Equal("add", changes[0].Fields["action"]);
        Assert.Equal("remove", changes[1].Fields["action"]);
        Assert.Equal("daniel", changes[0].Fields["who"]);
    }

    [Fact]
    public void AddingGitHostEntry_IsFlaggedAsDefeatingA6()
    {
        var audit = new InMemoryAuditLog();
        var allowlist = EgressAllowlist.WithDefaults(audit);

        allowlist.Add(new EgressAllowlistEntry("GitHub", "github.com", EgressEntryKind.GitHost), who: "daniel");

        Assert.True(allowlist.HasGitHostEntry);
        var entry = allowlist.Entries.Single(e => e.HostPattern == "github.com");
        Assert.True(entry.DefeatsA6);

        var change = audit.Read().Last(e => e.Type == EgressAllowlist.ChangeEventType);
        Assert.Equal("true", change.Fields["defeats_a6"]);
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("gitlab.com", true)]
    [InlineData("git.internal.corp", true)]
    [InlineData("api.anthropic.com", false)]
    [InlineData("registry.npmjs.org", false)]
    public void LooksLikeGitHost_Classifies(string host, bool expected)
    {
        Assert.Equal(expected, EgressAllowlistEntry.LooksLikeGitHost(host));
    }

    [Fact]
    public void Persistence_RoundTrips()
    {
        var audit = new InMemoryAuditLog();
        var original = EgressAllowlist.WithDefaults(audit);
        var json = original.ToPersistedForm();

        var restored = EgressAllowlist.FromPersistedForm(json, audit);
        Assert.Equal(
            original.Entries.Select(e => e.HostPattern).OrderBy(h => h),
            restored.Entries.Select(e => e.HostPattern).OrderBy(h => h));
    }
}
