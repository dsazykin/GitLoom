using System;
using System.Linq;
using Mainguard.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Graph;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Mainguard.Tests;

/// <summary>
/// TI-09 #5 — pinned refs persist (AppDbContext round-trip through PinnedRefService) and order
/// first into the CommitGraphRouter input (a pinned ref claims the left-most lane even when a
/// non-pinned tip appears earlier in the topo walk). The DB uses a shared in-memory SQLite
/// connection; each service call opens/closes its own context, so a round-trip across contexts
/// proves persistence the same way a restart would.
/// </summary>
public class PinnedRefsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PinnedRefsTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var ctx = new AppDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private PinnedRefService NewService() => new(() => new AppDbContext(_options));

    [Fact]
    public void Pin_ShouldPersist_AndReturnInPinOrder()
    {
        NewService().Pin("/repo", "main");
        NewService().Pin("/repo", "feature");

        // A fresh service instance = fresh contexts = simulated restart.
        var reloaded = NewService().GetPinnedRefs("/repo");

        Assert.Equal(new[] { "main", "feature" }, reloaded.Select(p => p.RefName));
        Assert.Equal(0, reloaded[0].Order);
        Assert.Equal(1, reloaded[1].Order);
    }

    [Fact]
    public void Pin_ShouldBeIdempotent()
    {
        var svc = NewService();
        svc.Pin("/repo", "main");
        svc.Pin("/repo", "main");

        Assert.Single(svc.GetPinnedRefs("/repo"));
        Assert.True(svc.IsPinned("/repo", "main"));
    }

    [Fact]
    public void Unpin_ShouldRemove()
    {
        var svc = NewService();
        svc.Pin("/repo", "main");
        svc.Unpin("/repo", "main");

        Assert.Empty(svc.GetPinnedRefs("/repo"));
        Assert.False(svc.IsPinned("/repo", "main"));
    }

    [Fact]
    public void PinnedRefs_ShouldBeScopedPerRepo()
    {
        var svc = NewService();
        svc.Pin("/repo-a", "main");

        Assert.Single(svc.GetPinnedRefs("/repo-a"));
        Assert.Empty(svc.GetPinnedRefs("/repo-b"));
    }

    [Fact]
    public void Migrations_ShouldApply_OnFreshDatabase_AndSupportPinnedRefRoundTrip()
    {
        // Proves the AddPinnedRefs migration applies the same way it does on app startup
        // (App.axaml.cs → Database.Migrate()), not just via the model (EnsureCreated).
        var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mainguard-mig-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var ctx = new AppDbContext(dbPath))
            {
                ctx.Database.Migrate();
            }

            new PinnedRefService(() => new AppDbContext(dbPath)).Pin("/repo", "main");
            Assert.True(new PinnedRefService(() => new AppDbContext(dbPath)).IsPinned("/repo", "main"));
        }
        finally
        {
            try { if (System.IO.File.Exists(dbPath)) System.IO.File.Delete(dbPath); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void PinnedRef_ShouldOrderFirst_InRouterInput()
    {
        // Two branch tips (F, M) sharing base B. In the topo stream F precedes M.
        var f = new GitCommitItem { Sha = "F", ParentShas = { "B" } };
        var m = new GitCommitItem { Sha = "M", ParentShas = { "B" } };
        var b = new GitCommitItem { Sha = "B" };
        var commits = new[] { f, m, b };
        var router = new CommitGraphRouter();

        // Baseline (nothing pinned): first-seen tip F takes lane 0.
        var baseline = router.RouteCommits(commits, new GraphFringeState(), null);
        Assert.Equal(0, Lane(baseline, "F"));
        Assert.Equal(1, Lane(baseline, "M"));

        // Pin M first → M claims lane 0 despite F appearing earlier in the walk.
        var pinned = router.RouteCommits(commits, new GraphFringeState(), new[] { "M" });
        Assert.Equal(0, Lane(pinned, "M"));
        Assert.True(Lane(pinned, "F") > 0);
    }

    [Fact]
    public void UnpinnedRoute_ShouldBeUnchanged_WhenPriorityListEmpty()
    {
        var f = new GitCommitItem { Sha = "F", ParentShas = { "B" } };
        var m = new GitCommitItem { Sha = "M", ParentShas = { "B" } };
        var b = new GitCommitItem { Sha = "B" };
        var commits = new[] { f, m, b };
        var router = new CommitGraphRouter();

        var viaNull = router.RouteCommits(commits, new GraphFringeState(), null);
        var viaEmpty = router.RouteCommits(commits, new GraphFringeState(), Array.Empty<string>());
        var viaOldOverload = router.RouteCommits(commits, new GraphFringeState());

        // Seeding is skipped when nothing is pinned: all three agree on lanes.
        foreach (var sha in new[] { "F", "M", "B" })
        {
            Assert.Equal(Lane(viaNull, sha), Lane(viaEmpty, sha));
            Assert.Equal(Lane(viaNull, sha), Lane(viaOldOverload, sha));
        }
    }

    private static int Lane(GraphRouteResult result, string sha)
        => result.Nodes.First(n => n.CommitSha == sha).LaneIndex;
}
