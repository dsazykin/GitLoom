using System;
using System.IO;
using Avalonia.Headless.XUnit;
using GitLoom.App.Editions;
using GitLoom.App.Services;
using Mainguard.Agents;
using Mainguard.Git.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;
using AppHost = GitLoom.App.App;

using Mainguard.Git;
namespace GitLoom.Tests;

/// <summary>
/// 1d — the Client launch path + dedicated Clone first-run (ADR-0001, docs/adr/0001-product-editions.md).
/// These guard the two decisions the Client edition adds to <c>App.OnFrameworkInitializationCompleted</c>:
/// (1) the edition→launch-composition split is the STRUCTURAL proof the Client launch is GitLoomOS-free —
/// the only branch that ever reaches keep-alive/resume-task/VM machinery is the Pro one; and (2) the
/// first-run gate routes an empty repo catalog to the first-run and a non-empty one straight to the shell.
/// </summary>
public class EditionLaunchTests
{
    // ---- (1) The Client launch is GitLoomOS-free ------------------------------------------------------

    // The composition decision is pure over the manifest, so this is the green, headless-free proof that
    // under the Client manifest the launch picks the ClientPlain path — the ONE path in App that does NOT
    // call EnsureKeepAlive / SweepResumeTaskAtStartup / wire the desktop.Exit VM-stop or DecideLaunchRoute
    // (all of which live only in StartProDesktop). Pro keeps the GitLoomOS path.
    [Fact]
    public void DecideLaunchComposition_IsGitLoomOsFreeUnderClient_AndProUnderPro()
    {
        Assert.Equal(AppHost.LaunchComposition.ClientPlain, AppHost.DecideLaunchComposition(new ClientManifest()));
        Assert.Equal(AppHost.LaunchComposition.ProGitLoomOs, AppHost.DecideLaunchComposition(new ProManifest()));

        // Cross-check the manifest wiring the decision keys off (Client has no agent platform; its first-run
        // is the plain-client clone flow, never GitLoomOS provisioning).
        Assert.False(new ClientManifest().HasAgentPlatform);
        Assert.Equal(EditionFirstRun.ClientClone, new ClientManifest().FirstRun);
        Assert.True(new ProManifest().HasAgentPlatform);
        Assert.Equal(EditionFirstRun.GitLoomOsProvisioning, new ProManifest().FirstRun);
    }

    // ---- (2) The first-run gate: empty catalog → first-run, non-empty → shell -------------------------

    // The pure gate mapping, both directions, no database needed.
    [Fact]
    public void ClientLaunchTargetFor_MapsEmptyToFirstRun_AndNonEmptyToShell()
    {
        Assert.Equal(AppHost.ClientLaunchTarget.FirstRun, AppHost.ClientLaunchTargetFor(catalogEmpty: true));
        Assert.Equal(AppHost.ClientLaunchTarget.Shell, AppHost.ClientLaunchTargetFor(catalogEmpty: false));
    }

    // The store read the gate stands on (RepoCatalog.IsEmpty) reacts to the catalog. Exercised against a
    // temp SQLite DB via the injected-context overload so it is fully deterministic and never touches the
    // app's shared DB. A freshly migrated DB has the seeded categories but zero repositories → empty.
    [Fact]
    public void RepoCatalog_IsEmpty_TracksTheStore()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"gitloom-1d-gate-{Guid.NewGuid():N}.db");
        try
        {
            using (var seed = new AppDbContext(dbPath))
                seed.Database.Migrate();

            using (var db = new AppDbContext(dbPath))
                Assert.True(RepoCatalog.IsEmpty(db)); // fresh catalog → first run

            using (var db = new AppDbContext(dbPath))
            {
                db.Repositories.Add(new Repository
                {
                    Path = "/tmp/gitloom-1d-fixture",
                    DisplayName = "fixture",
                    CategoryId = 1, // the migration seeds category 1 (Personal)
                    LastAccessed = DateTime.UtcNow,
                });
                db.SaveChanges();
            }

            using (var db = new AppDbContext(dbPath))
                Assert.False(RepoCatalog.IsEmpty(db)); // a registered repo → shell
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    // End-to-end composition over the REAL (headless-app-migrated) default store: DecideClientLaunchTarget
    // joins RepoCatalog.IsEmpty() to the pure mapping. Asserts the join is consistent with the live store
    // without mutating it (the assembly runs serially, so the two reads cannot interleave with another test).
    [AvaloniaFact]
    public void DecideClientLaunchTarget_ComposesGateOverTheLiveStore()
    {
        var expected = RepoCatalog.IsEmpty() ? AppHost.ClientLaunchTarget.FirstRun : AppHost.ClientLaunchTarget.Shell;
        Assert.Equal(expected, AppHost.DecideClientLaunchTarget());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leaked temp DB file is harmless; never fail the test on cleanup.
        }
    }
}
