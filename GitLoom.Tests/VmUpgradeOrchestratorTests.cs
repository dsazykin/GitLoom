using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The tier-2 in-place VM upgrade (P2-21 §3.6). Covers the pure offer policy (proper version
/// compare — a newer-than-app payload is NEVER downgraded), the release-stamp parser, the new
/// command builders' exact argv, the <see cref="VmUpgradeCheck"/> detection flow (daemon answer →
/// in-distro stamp fallback → no-offer on unknowns), and the <see cref="VmUpgradeOrchestrator"/>
/// over a fake <see cref="IWslRunner"/>: the full happy-path order, the invariant-3 launch blocker
/// (migrate + validate strictly precede any old-distro mutation), failure-before-retire leaving the
/// old distro untouched (staging cleaned up, daemon restarted), the resilient promote (bounded
/// move retry, then the copy-then-cleanup fallback whose staging unregister comes strictly AFTER
/// the verified copy), and the typed stranded-state error after the retire — terminal only when
/// BOTH promote strategies fail, always naming the surviving data-bearing VHDX.
/// </summary>
public class VmUpgradeOrchestratorTests
{
    // ---- VmUpgradePolicy: the pure offer decision ---------------------------------------------

    [Fact]
    public void Policy_OffersUpgrade_OnlyWhenInstalledIsOlder()
    {
        Assert.True(VmUpgradePolicy.IsUpgradeAvailable("0.1.0", "0.2.0"));
        Assert.True(VmUpgradePolicy.IsUpgradeAvailable("0.9.0", "0.10.0")); // numeric, not lexicographic
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable("0.2.0", "0.2.0"));
    }

    [Fact]
    public void Policy_NeverDowngrades_AnInstalledPayloadNewerThanTheApp()
    {
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable("0.3.0", "0.2.0"));
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable("1.0.0", "0.99.99"));
    }

    [Fact]
    public void Policy_GarbageOrMissingVersions_NeverOffer()
    {
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable(null, "0.2.0"));
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable("", "0.2.0"));
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable("banana", "0.2.0"));
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable("0.1.0", null));
        Assert.False(VmUpgradePolicy.IsUpgradeAvailable("0.1.0", "not-a-version"));
    }

    [Fact]
    public void Policy_ToleratesBuildMetadataAndPrereleaseSuffixes()
    {
        Assert.True(VmUpgradePolicy.IsUpgradeAvailable("0.1.0+abc123", "0.2.0-rc.1"));
        Assert.Equal(new Version(1, 2, 3), VmUpgradePolicy.TryParseVersion(" 1.2.3+build.9 "));
        Assert.Null(VmUpgradePolicy.TryParseVersion("v1.2.3")); // no silent guess on a prefix
    }

    // ---- GitLoomOsReleaseStamp: the stamp parser ----------------------------------------------

    [Fact]
    public void ReleaseStamp_ParsesTheVersionLine_AmongOtherKeys()
    {
        var content = "GITLOOMOS_VERSION=0.2.0\r\nBUILD_INPUTS_HASH=abc\nTARBALL_SHA256=def\n";
        Assert.Equal("0.2.0", GitLoomOsReleaseStamp.ParseVersion(content));
    }

    [Fact]
    public void ReleaseStamp_MissingKeyOrEmpty_YieldsEmpty()
    {
        Assert.Equal("", GitLoomOsReleaseStamp.ParseVersion("BUILD_INPUTS_HASH=abc\n"));
        Assert.Equal("", GitLoomOsReleaseStamp.ParseVersion(""));
        Assert.Equal("", GitLoomOsReleaseStamp.ParseVersion(null));
    }

    // ---- The new command builders: exact argv -------------------------------------------------

    [Fact]
    public void Builders_EmitTheExactExpectedArgv()
    {
        Assert.Equal(
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "systemctl", "stop", "gitloomd" },
            VmUpgradeCommands.StopUnitInStaging());
        Assert.Equal(
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "test", "-d", "/home/gitloom/gitloom" },
            VmUpgradeCommands.ProbeOldDirectory("/home/gitloom/gitloom"));
        Assert.Equal(
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "tar", "-C", "/home/gitloom/gitloom", "-cpf", "/mnt/c/t/u.tar", "." },
            VmUpgradeCommands.ExportTreeToTar("/home/gitloom/gitloom", "/mnt/c/t/u.tar"));
        Assert.Equal(
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "tar", "-C", "/home/gitloom/.gitloom", "--exclude=./daemon.token", "--exclude=./logs", "-cpf", "/mnt/c/t/s.tar", "." },
            VmUpgradeCommands.ExportTreeToTar("/home/gitloom/.gitloom", "/mnt/c/t/s.tar", excludeDaemonToken: true));
        Assert.Equal(
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "tar", "-C", "/home/gitloom/gitloom", "-xpf", "/mnt/c/t/u.tar" },
            VmUpgradeCommands.ExtractTreeFromTar("/home/gitloom/gitloom", "/mnt/c/t/u.tar"));
        Assert.Equal(
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "find", "/home/gitloom/gitloom", "-mindepth", "1", "-maxdepth", "3" },
            VmUpgradeCommands.EnumerateOldTree("/home/gitloom/gitloom"));
        Assert.Equal(
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "find", "/home/gitloom/gitloom", "-mindepth", "1", "-maxdepth", "3" },
            VmUpgradeCommands.EnumerateStagingTree("/home/gitloom/gitloom"));
        Assert.Equal(
            new[] { "-d", "GitLoomEnv", "--", "cat", "/etc/gitloomos-release" },
            VmUpgradeCommands.ReadInstalledReleaseStamp());
    }

    // ---- FindMissingFromListings: the pure in-distro validation diff --------------------------

    [Fact]
    public void FindMissingFromListings_ReportsExactlyTheAbsentPaths()
    {
        var oldListing = "/home/gitloom/gitloom/repos\n/home/gitloom/gitloom/repos/a.git\n/home/gitloom/gitloom/worktrees/h/agent-1\n";
        var stagingListing = "/home/gitloom/gitloom/repos\n/home/gitloom/gitloom/repos/a.git\n";

        var missing = VmUpgradeMigrator.FindMissingFromListings(oldListing, stagingListing);

        Assert.Equal(new[] { "/home/gitloom/gitloom/worktrees/h/agent-1" }, missing);
        Assert.Empty(VmUpgradeMigrator.FindMissingFromListings(oldListing, oldListing));
    }

    // ---- VmUpgradeCheck: the detection flow ---------------------------------------------------

    [Fact]
    public async Task Check_Offers_WhenTheDaemonReportsAnOlderPayload()
    {
        var stamp = WriteTempStamp("GITLOOMOS_VERSION=0.2.0\n");
        var wsl = new RecordingWslRunner();
        var log = new List<string>();

        var result = await VmUpgradeCheck.RunAsync(
            stamp,
            _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.1.0")),
            wsl, log.Add, CancellationToken.None);

        Assert.True(result.OfferUpgrade);
        Assert.Equal("0.1.0", result.InstalledVersion);
        Assert.Equal("0.2.0", result.ExpectedVersion);
        Assert.Empty(wsl.Calls); // the daemon answered — no in-distro fallback read
    }

    [Fact]
    public async Task Check_FallsBackToTheInDistroStamp_WhenTheDaemonIsDown()
    {
        var stamp = WriteTempStamp("GITLOOMOS_VERSION=0.2.0\n");
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("cat")
                ? new WslRunResult(0, "GITLOOMOS_VERSION=0.1.0\n", "")
                : new WslRunResult(0, "", ""),
        };
        var log = new List<string>();

        var result = await VmUpgradeCheck.RunAsync(
            stamp,
            _ => throw new InvalidOperationException("connection refused"),
            wsl, log.Add, CancellationToken.None);

        Assert.True(result.OfferUpgrade);
        Assert.Contains(wsl.Calls, c => c.SequenceEqual(
            new[] { "-d", "GitLoomEnv", "--", "cat", "/etc/gitloomos-release" }));
    }

    [Fact]
    public async Task Check_NeverOffers_WhenTheBundledStampIsMissing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), "gitloom-nostamp-" + Guid.NewGuid().ToString("N"));
        var log = new List<string>();

        var result = await VmUpgradeCheck.RunAsync(
            missingPath,
            _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.1.0")),
            new RecordingWslRunner(), log.Add, CancellationToken.None);

        Assert.False(result.OfferUpgrade);
        Assert.Contains(log, l => l.Contains("no readable payload stamp"));
    }

    [Fact]
    public async Task Check_NeverOffers_WhenTheInstalledVersionStaysUnknown()
    {
        var stamp = WriteTempStamp("GITLOOMOS_VERSION=0.2.0\n");
        var wsl = new RecordingWslRunner { Responder = _ => new WslRunResult(1, "", "no such file") };
        var log = new List<string>();

        var result = await VmUpgradeCheck.RunAsync(
            stamp,
            _ => Task.FromResult<DaemonVersionInfo?>(null), // pre-GetDaemonInfo daemon
            wsl, log.Add, CancellationToken.None);

        Assert.False(result.OfferUpgrade);
        Assert.Contains(log, l => l.Contains("installed payload version unknown"));
    }

    [Fact]
    public async Task Check_NeverOffers_WhenTheInstalledPayloadIsNewer()
    {
        var stamp = WriteTempStamp("GITLOOMOS_VERSION=0.2.0\n");
        var log = new List<string>();

        var result = await VmUpgradeCheck.RunAsync(
            stamp,
            _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.3.0")),
            new RecordingWslRunner(), log.Add, CancellationToken.None);

        Assert.False(result.OfferUpgrade);
    }

    // ---- The orchestrator over the IWslRunner seam --------------------------------------------

    private static VmUpgradeOptions TestOptions() => new(
        TarballPath: @"C:\Apps\GitLoom\payload\GitLoomOS.tar.gz",
        StagingInstallDir: @"C:\Data\GitLoom\vm-staging",
        CanonicalInstallDir: @"C:\Data\GitLoom\vm");

    [Fact]
    public async Task Upgrade_HappyPath_RunsTheExactPlanOrder_AndPromotesTheMovedVhdx()
    {
        var wsl = HealthyRunner();
        var fs = new FakeHostFileSystem();
        var options = TestOptions();

        var result = await new VmUpgradeOrchestrator(wsl, fs)
            .UpgradeAsync(options, progress: null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(VmUpgradeFailureKind.None, result.FailureKind);

        var userTar = VmUpgradeOrchestrator.ToVmPath(Path.Combine(fs.TempDir, "user-data.tar"));
        var stateTar = VmUpgradeOrchestrator.ToVmPath(Path.Combine(fs.TempDir, "daemon-state.tar"));
        var promotedVhdx = Path.Combine(options.CanonicalInstallDir, "ext4.vhdx");
        var expected = new List<string[]>
        {
            // import-staging (stale-staging hygiene first)
            new[] { "--terminate", "GitLoomEnv-staging" },
            new[] { "--unregister", "GitLoomEnv-staging" },
            new[] { "--import", "GitLoomEnv-staging", options.StagingInstallDir, options.TarballPath, "--version", "2" },
            // migrate-user-data (both daemons quiesced; tar via the host temp file)
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "systemctl", "stop", "gitloomd" },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "systemctl", "stop", "gitloomd" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "test", "-d", "/home/gitloom/gitloom" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "tar", "-C", "/home/gitloom/gitloom", "-cpf", userTar, "." },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "mkdir", "-p", "/home/gitloom/gitloom" },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "tar", "-C", "/home/gitloom/gitloom", "-xpf", userTar },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "chown", "-R", "gitloom:gitloom", "/home/gitloom/gitloom" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "test", "-d", "/home/gitloom/.gitloom" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "tar", "-C", "/home/gitloom/.gitloom", "--exclude=./daemon.token", "--exclude=./logs", "-cpf", stateTar, "." },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "mkdir", "-p", "/home/gitloom/.gitloom" },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "tar", "-C", "/home/gitloom/.gitloom", "-xpf", stateTar },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "chown", "-R", "gitloom:gitloom", "/home/gitloom/.gitloom" },
            // validate-migration (enumerate old + staging, per tree)
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "find", "/home/gitloom/gitloom", "-mindepth", "1", "-maxdepth", "3" },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "find", "/home/gitloom/gitloom", "-mindepth", "1", "-maxdepth", "3" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "find", "/home/gitloom/.gitloom", "-mindepth", "1", "-maxdepth", "3" },
            new[] { "-d", "GitLoomEnv-staging", "-u", "root", "--", "find", "/home/gitloom/.gitloom", "-mindepth", "1", "-maxdepth", "3" },
            // retire (only now) + promote
            new[] { "--terminate", "GitLoomEnv" },
            new[] { "--unregister", "GitLoomEnv" },
            new[] { "--terminate", "GitLoomEnv-staging" },
            new[] { "--unregister", "GitLoomEnv-staging" },
            new[] { "--import-in-place", "GitLoomEnv", promotedVhdx, "--version", "2" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "systemctl", "start", "gitloomd" },
        };

        Assert.Equal(expected.Count, wsl.Calls.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i], wsl.Calls[i]);

        // The VHDX was moved out of staging BEFORE staging's final unregister could delete it —
        // one first-try move, the copy fallback never touched.
        Assert.Equal(
            (Path.Combine(options.StagingInstallDir, "ext4.vhdx"), promotedVhdx),
            Assert.Single(fs.Moves));
        Assert.Equal(1, fs.MoveAttemptCount);
        Assert.Empty(fs.Copies);
        Assert.Equal("move", result.PromoteStrategy);
        Assert.Contains(fs.DeletedDirs, d => d == fs.TempDir);        // tar transport cleaned up
        Assert.Contains(fs.DeletedDirs, d => d == options.StagingInstallDir);
    }

    [Fact]
    public async Task Upgrade_MigrateAndValidate_StrictlyPrecede_AnyOldDistroMutation()
    {
        var wsl = HealthyRunner();
        await new VmUpgradeOrchestrator(wsl, new FakeHostFileSystem())
            .UpgradeAsync(TestOptions(), progress: null, CancellationToken.None);

        int IndexOf(Func<IReadOnlyList<string>, bool> match) =>
            wsl.Calls.ToList().FindIndex(c => match(c));

        var firstOldMutation = IndexOf(c =>
            (c[0] == "--terminate" || c[0] == "--unregister") && c[1] == "GitLoomEnv");
        var lastMigrate = wsl.Calls.ToList().FindLastIndex(c => c.Contains("tar"));
        var lastValidate = wsl.Calls.ToList().FindLastIndex(c => c.Contains("find"));

        Assert.True(firstOldMutation > lastMigrate, "old distro was mutated before migration finished");
        Assert.True(firstOldMutation > lastValidate, "old distro was mutated before validation finished");
    }

    [Fact]
    public async Task Upgrade_ValidationFindsMissingPaths_FailsBeforeRetire_AndRestoresTheOldDistro()
    {
        // Staging's find answers a subset of the old distro's — a repo went missing in migration.
        var wsl = new RecordingWslRunner
        {
            Responder = args =>
            {
                if (args.Contains("find"))
                {
                    return args[1] == "GitLoomEnv"
                        ? new WslRunResult(0, "/home/gitloom/gitloom/repos/a.git\n/home/gitloom/gitloom/repos/b.git\n", "")
                        : new WslRunResult(0, "/home/gitloom/gitloom/repos/a.git\n", "");
                }

                return new WslRunResult(0, "", "");
            },
        };

        var result = await new VmUpgradeOrchestrator(wsl, new FakeHostFileSystem())
            .UpgradeAsync(TestOptions(), progress: null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VmUpgradeFailureKind.OldDistroIntact, result.FailureKind);
        Assert.Contains("b.git", result.Message);

        // Invariant 3: the old distro was NEVER terminated or unregistered…
        Assert.DoesNotContain(wsl.Calls, c => c[0] == "--terminate" && c[1] == "GitLoomEnv");
        Assert.DoesNotContain(wsl.Calls, c => c[0] == "--unregister" && c[1] == "GitLoomEnv");
        // …staging was cleaned up, and the old daemon was started again.
        Assert.Contains(wsl.Calls, c => c[0] == "--unregister" && c[1] == "GitLoomEnv-staging");
        Assert.Equal(
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "systemctl", "start", "gitloomd" },
            wsl.Calls[^1]);
    }

    [Fact]
    public async Task Upgrade_ImportStagingFails_LeavesTheOldDistroUntouched()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args => args[0] == "--import"
                ? new WslRunResult(1, "", "not enough disk space")
                : new WslRunResult(0, "", ""),
        };

        var result = await new VmUpgradeOrchestrator(wsl, new FakeHostFileSystem())
            .UpgradeAsync(TestOptions(), progress: null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VmUpgradeFailureKind.OldDistroIntact, result.FailureKind);
        Assert.Contains("disk space", result.Message);
        Assert.DoesNotContain(wsl.Calls, c => c[0] == "--terminate" && c[1] == "GitLoomEnv");
        Assert.DoesNotContain(wsl.Calls, c => c[0] == "--unregister" && c[1] == "GitLoomEnv");
    }

    [Fact]
    public async Task Upgrade_PromoteFailsAfterRetire_SurfacesTheTypedStrandedError_NamingTheVhdx()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args => args[0] == "--import-in-place"
                ? new WslRunResult(1, "", "WSL_E_IMPORT_FAILED")
                : new WslRunResult(0, "", ""),
        };
        var fs = new FakeHostFileSystem();
        var options = TestOptions();

        var result = await new VmUpgradeOrchestrator(wsl, fs)
            .UpgradeAsync(options, progress: null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VmUpgradeFailureKind.StrandedAfterRetire, result.FailureKind);
        // The error names EXACTLY where the migrated data lives (the moved VHDX) + the recovery command.
        var promotedVhdx = Path.Combine(options.CanonicalInstallDir, "ext4.vhdx");
        Assert.Equal(promotedVhdx, result.StagingVhdxPath);
        Assert.Contains(promotedVhdx, result.Message);
        Assert.Contains("--import-in-place GitLoomEnv", result.Message);
        // The data-bearing VHDX is never deleted and staging's install dir is not purged here.
        Assert.DoesNotContain(fs.DeletedDirs, d => d == options.StagingInstallDir);
    }

    [Fact]
    public async Task Upgrade_VhdxMoveFailsOnce_TheBoundedRetrySucceeds_WithoutTheCopyFallback()
    {
        var wsl = HealthyRunner();
        var lines = new List<string>();
        var fs = new FakeHostFileSystem { MoveFailuresBeforeSuccess = 1 };

        var result = await new VmUpgradeOrchestrator(wsl, fs, moveRetryDelay: TimeSpan.Zero)
            .UpgradeAsync(TestOptions(), new SynchronousProgress(lines.Add), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("move", result.PromoteStrategy);
        Assert.Equal(2, fs.MoveAttemptCount);      // fail, retry, done — within the bound
        Assert.Single(fs.Moves);
        Assert.Empty(fs.Copies);                   // the fallback never engaged
        Assert.Contains(lines, l => l.Contains("move attempt 1/"));
        Assert.Contains(wsl.Calls, c => c[0] == "--import-in-place" && c[1] == "GitLoomEnv");
    }

    [Fact]
    public async Task Upgrade_VhdxMoveExhausted_FallsBackToCopy_UnregisteringStagingOnlyAfterTheVerifiedCopy()
    {
        // The field incident: WSL's shared utility VM holds the staging VHDX for as long as ANY
        // distro keeps it alive — the move NEVER succeeds, however long we retry.
        var journal = new List<string>();
        var wsl = new RecordingWslRunner
        {
            Responder = args =>
            {
                if (args[0] == "--unregister" && args[1] == "GitLoomEnv-staging")
                    journal.Add("unregister-staging");
                if (args[0] == "--import-in-place")
                    journal.Add("import-in-place");
                return args.Contains("find")
                    ? new WslRunResult(0, "/x/repos/a.git\n", "")
                    : new WslRunResult(0, "", "");
            },
        };
        var fs = new FakeHostFileSystem
        {
            MoveThrows = new IOException("being used by another process"),
            Journal = journal,
        };
        var options = TestOptions();

        var result = await new VmUpgradeOrchestrator(wsl, fs, moveRetryDelay: TimeSpan.Zero)
            .UpgradeAsync(options, progress: null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("copy-then-cleanup", result.PromoteStrategy);
        Assert.Equal(VmUpgradeOrchestrator.MoveAttempts, fs.MoveAttemptCount); // the bound, then the fallback
        var promotedVhdx = Path.Combine(options.CanonicalInstallDir, "ext4.vhdx");
        Assert.Equal(
            (Path.Combine(options.StagingInstallDir, "ext4.vhdx"), promotedVhdx),
            Assert.Single(fs.Copies));

        // The copy path's REORDER: the verified copy comes strictly BEFORE the staging unregister
        // (which deletes the original VHDX), and the import-in-place after that.
        var copyIndex = journal.IndexOf("copy");
        var cleanupUnregisterIndex = journal.LastIndexOf("unregister-staging");
        var importIndex = journal.IndexOf("import-in-place");
        Assert.True(copyIndex >= 0, "the copy fallback never ran");
        Assert.True(cleanupUnregisterIndex > copyIndex, "staging was unregistered before the copy was verified");
        Assert.True(importIndex > cleanupUnregisterIndex, "import-in-place ran before staging's cleanup unregister");
        Assert.Contains(wsl.Calls, c => c[0] == "--import-in-place" && c[2] == promotedVhdx);
    }

    [Fact]
    public async Task Upgrade_MoveExhaustedAndCopyVerificationFails_IsStranded_NamingBothFailures()
    {
        var wsl = HealthyRunner();
        var options = TestOptions();
        var fs = new FakeHostFileSystem
        {
            MoveThrows = new IOException("sharing violation"),
            // The copy "succeeds" but the canonical file comes up short — verification must fail.
            LengthOf = path => path.StartsWith(options.StagingInstallDir, StringComparison.Ordinal) ? 4096 : 1024,
        };

        var result = await new VmUpgradeOrchestrator(wsl, fs, moveRetryDelay: TimeSpan.Zero)
            .UpgradeAsync(options, progress: null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VmUpgradeFailureKind.StrandedAfterRetire, result.FailureKind);
        Assert.Null(result.PromoteStrategy);       // neither strategy landed the VHDX
        // The terminal message names BOTH failures and points at the intact staging VHDX.
        Assert.Contains("sharing violation", result.Message);
        Assert.Contains("copy fallback also failed", result.Message);
        var stagingVhdx = Path.Combine(options.StagingInstallDir, "ext4.vhdx");
        Assert.Equal(stagingVhdx, result.StagingVhdxPath);
        Assert.Contains(stagingVhdx, result.Message);
        // The data-bearing VHDX survives: staging's dir is never purged on this path.
        Assert.DoesNotContain(fs.DeletedDirs, d => d == options.StagingInstallDir);
    }

    [Fact]
    public async Task Upgrade_CopySucceedsButImportFails_IsStranded_PointingAtTheCanonicalVhdx()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args => args[0] == "--import-in-place"
                ? new WslRunResult(1, "", "WSL_E_IMPORT_FAILED")
                : args.Contains("find")
                    ? new WslRunResult(0, "/x/repos/a.git\n", "")
                    : new WslRunResult(0, "", ""),
        };
        var options = TestOptions();
        var fs = new FakeHostFileSystem { MoveThrows = new IOException("being used by another process") };

        var result = await new VmUpgradeOrchestrator(wsl, fs, moveRetryDelay: TimeSpan.Zero)
            .UpgradeAsync(options, progress: null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(VmUpgradeFailureKind.StrandedAfterRetire, result.FailureKind);
        Assert.Equal("copy-then-cleanup", result.PromoteStrategy);
        // The canonical copy is the surviving artifact (staging's original was deleted by the
        // post-copy unregister) — the message and the typed path must point THERE.
        var promotedVhdx = Path.Combine(options.CanonicalInstallDir, "ext4.vhdx");
        Assert.Equal(promotedVhdx, result.StagingVhdxPath);
        Assert.Contains(promotedVhdx, result.Message);
        Assert.Contains("--import-in-place GitLoomEnv", result.Message);
        Assert.DoesNotContain(fs.DeletedDirs, d => d == options.StagingInstallDir);
    }

    [Fact]
    public async Task Upgrade_WithNoUserData_SkipsTheTarTransport_AndStillUpgrades()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("test")
                ? new WslRunResult(1, "", "") // neither ~/gitloom nor ~/.gitloom exists yet
                : new WslRunResult(0, "", ""),
        };

        var result = await new VmUpgradeOrchestrator(wsl, new FakeHostFileSystem())
            .UpgradeAsync(TestOptions(), progress: null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("tar"));
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("find")); // nothing migrated → nothing to validate
        Assert.Contains(wsl.Calls, c => c[0] == "--import-in-place" && c[1] == "GitLoomEnv");
    }

    [Fact]
    public async Task Upgrade_ReportsEveryPlanStepDescription_InOrder()
    {
        var lines = new List<string>();
        var progress = new SynchronousProgress(lines.Add);

        await new VmUpgradeOrchestrator(HealthyRunner(), new FakeHostFileSystem())
            .UpgradeAsync(TestOptions(), progress, CancellationToken.None);

        var stepDescriptions = VmUpgradePlan.Steps().Select(s => s.Description).ToList();
        var reportedSteps = lines.Where(stepDescriptions.Contains).ToList();
        Assert.Equal(stepDescriptions, reportedSteps);
    }

    // ---- fakes --------------------------------------------------------------------------------

    /// <summary>A runner where everything succeeds and both distros' find answers agree.</summary>
    private static RecordingWslRunner HealthyRunner() => new()
    {
        Responder = args => args.Contains("find")
            ? new WslRunResult(0, "/x/repos/a.git\n/x/worktrees/h/agent-1\n", "")
            : new WslRunResult(0, "", ""),
    };

    private static string WriteTempStamp(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "gitloom-stamp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    private sealed class RecordingWslRunner : IWslRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();
        public Func<IReadOnlyList<string>, WslRunResult>? Responder { get; set; }

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(Responder?.Invoke(args) ?? new WslRunResult(0, "", ""));
        }
    }

    private sealed class FakeHostFileSystem : IVmUpgradeHostFileSystem
    {
        public string TempDir { get; } = @"C:\Temp\gitloom-vm-upgrade-test";
        public List<(string From, string To)> Moves { get; } = new();
        public List<(string From, string To)> Copies { get; } = new();
        public List<string> CreatedDirs { get; } = new();
        public List<string> DeletedDirs { get; } = new();
        public int MoveAttemptCount { get; private set; }

        /// <summary>Every move attempt throws this (the permanent WSL utility-VM hold).</summary>
        public Exception? MoveThrows { get; set; }

        /// <summary>The first N move attempts throw (the transient hold the retry covers).</summary>
        public int MoveFailuresBeforeSuccess { get; set; }

        public Exception? CopyThrows { get; set; }

        /// <summary>Per-path file length (copy verification); every file is 4096 bytes unless set.</summary>
        public Func<string, long>? LengthOf { get; set; }

        /// <summary>Optional shared cross-seam event log (with the wsl fake) for ordering asserts.</summary>
        public List<string>? Journal { get; set; }

        public string CreateTempDirectory() => TempDir;

        public void MoveFile(string sourcePath, string destinationPath)
        {
            MoveAttemptCount++;
            Journal?.Add("move");
            if (MoveThrows is not null)
                throw MoveThrows;
            if (MoveAttemptCount <= MoveFailuresBeforeSuccess)
                throw new IOException("the process cannot access the file");
            Moves.Add((sourcePath, destinationPath));
        }

        public void CopyFile(string sourcePath, string destinationPath)
        {
            Journal?.Add("copy");
            if (CopyThrows is not null)
                throw CopyThrows;
            Copies.Add((sourcePath, destinationPath));
        }

        public long GetFileLength(string path) => LengthOf?.Invoke(path) ?? 4096;

        public void CreateDirectory(string path) => CreatedDirs.Add(path);

        public void DeleteDirectoryBestEffort(string path) => DeletedDirs.Add(path);
    }

    /// <summary>Reports inline on the calling thread — no SynchronizationContext lottery.</summary>
    private sealed class SynchronousProgress : IProgress<string>
    {
        private readonly Action<string> _apply;

        public SynchronousProgress(Action<string> apply) => _apply = apply;

        public void Report(string value) => _apply(value);
    }
}
