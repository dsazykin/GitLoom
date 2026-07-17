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
/// The tier-1 daemon fast-path (field outage 2026-07: the daemon baked into the GitLoomOS tarball
/// never advances with the app, so every new RPC answers Unimplemented — the coordinator-CLI-picker
/// skew). Covers the pure skew decision, the /mnt path translation, the exact in-distro refresh
/// command sequence over a fake <see cref="IWslRunner"/> (incl. the rollback dir and failure
/// recovery), the G-12 no-VM-wide-verbs invariant, and the <see cref="DaemonAutoRefresh"/>
/// orchestration (daemon-down skip, Unimplemented-as-skew, missing-payload skip).
/// </summary>
public class DaemonUpdaterTests
{
    // ---- DaemonUpdatePolicy: the pure skew decision -------------------------------------------

    [Fact]
    public void RefreshNeeded_WhenDaemonPredatesTheRpc()
    {
        // null == the daemon answered Unimplemented — the skew signal itself.
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", daemonInfo: null));
    }

    [Fact]
    public void RefreshNeeded_WhenDaemonCannotNameItsVersion()
    {
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("", "0.1.0")));
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("   ", "")));
    }

    [Fact]
    public void RefreshNeeded_WhenVersionsDiffer()
    {
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("0.1.0", "0.1.0")));
    }

    [Fact]
    public void RefreshNotNeeded_WhenVersionsMatch()
    {
        Assert.False(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("0.2.0", "0.1.0")));
    }

    [Fact]
    public void RefreshDecision_IgnoresSemVerBuildMetadata()
    {
        // CI builds append "+<sha>"; the release train (csproj Version), not the commit, decides.
        Assert.False(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0+abc123", new DaemonVersionInfo("0.2.0+def456", "")));
        Assert.False(DaemonUpdatePolicy.IsRefreshNeeded("0.2.0", new DaemonVersionInfo("0.2.0+def456", "")));
        Assert.True(DaemonUpdatePolicy.IsRefreshNeeded("0.3.0+abc123", new DaemonVersionInfo("0.2.0+abc123", "")));
    }

    // ---- /mnt path translation ----------------------------------------------------------------

    [Fact]
    public void ToVmPath_TranslatesTheWindowsPayloadDir_ToItsDrvfsForm()
    {
        Assert.Equal(
            "/mnt/c/Program Files/GitLoom/payload/daemon",
            DaemonUpdater.ToVmPath(@"C:\Program Files\GitLoom\payload\daemon"));
    }

    [Fact]
    public void ToVmPath_PassesNativeLinuxPathsThrough()
    {
        // The Linux CI leg / tests hand native paths — untouched.
        Assert.Equal("/tmp/payload/daemon", DaemonUpdater.ToVmPath("/tmp/payload/daemon"));
    }

    // ---- The refresh command sequence over the IWslRunner seam --------------------------------

    [Fact]
    public async Task Refresh_RunsTheExactInDistroSequence_WithTheRollbackSwap()
    {
        var wsl = new RecordingWslRunner();
        var result = await new DaemonUpdater(wsl)
            .RefreshAsync(@"C:\Apps\GitLoom\payload\daemon", CancellationToken.None);

        Assert.True(result.Succeeded);
        var expected = new[]
        {
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "systemctl", "stop", "gitloomd" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "rm", "-rf", "/opt/gitloom.new" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "mkdir", "-p", "/opt/gitloom.new" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "cp", "-r", "/mnt/c/Apps/GitLoom/payload/daemon/.", "/opt/gitloom.new/" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "test", "-e", "/opt/gitloom.new/GitLoom.Server" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "mv", "/opt/gitloom.new/GitLoom.Server", "/opt/gitloom.new/gitloomd" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "chmod", "0755", "/opt/gitloom.new/gitloomd" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "rm", "-rf", "/opt/gitloom.old" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "mv", "/opt/gitloom", "/opt/gitloom.old" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "mv", "/opt/gitloom.new", "/opt/gitloom" },
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "systemctl", "start", "gitloomd" },
        };
        Assert.Equal(expected.Length, wsl.Calls.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], wsl.Calls[i]);
        }
    }

    [Fact]
    public async Task Refresh_SkipsTheApphostRename_WhenThePayloadShipsItAlreadyRenamed()
    {
        // A build.sh-produced payload already carries `gitloomd` — the probe misses, no mv.
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("test")
                ? new WslRunResult(1, "", "")
                : new WslRunResult(0, "", ""),
        };

        var result = await new DaemonUpdater(wsl).RefreshAsync(@"C:\x\payload\daemon", CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("/opt/gitloom.new/GitLoom.Server") && c.Contains("mv"));
        Assert.Contains(wsl.Calls, c => c.Contains("chmod") && c.Contains("/opt/gitloom.new/gitloomd"));
    }

    [Fact]
    public async Task Refresh_WhenThePromoteFails_RestoresTheRollback_AndRestartsTheUnit()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args =>
                args.Contains("mv") && args.Contains("/opt/gitloom.new") && args.Contains("/opt/gitloom")
                    ? new WslRunResult(1, "", "mv: cannot move")
                    : new WslRunResult(0, "", ""),
        };

        var result = await new DaemonUpdater(wsl).RefreshAsync(@"C:\x\payload\daemon", CancellationToken.None);

        Assert.False(result.Succeeded);
        // Recovery: the retired install comes back, and the unit is started again.
        Assert.Contains(wsl.Calls, c => c.SequenceEqual(
            new[] { "-d", "GitLoomEnv", "-u", "root", "--", "mv", "/opt/gitloom.old", "/opt/gitloom" }));
        Assert.Equal(new[] { "-d", "GitLoomEnv", "-u", "root", "--", "systemctl", "start", "gitloomd" }, wsl.Calls[^1]);
    }

    [Fact]
    public async Task Refresh_WhenTheStagingCopyFails_NeverTouchesTheInstallDir_AndRestartsTheUnit()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("cp")
                ? new WslRunResult(1, "", "cp: no such file or directory")
                : new WslRunResult(0, "", ""),
        };

        var result = await new DaemonUpdater(wsl).RefreshAsync(@"C:\x\payload\daemon", CancellationToken.None);

        Assert.False(result.Succeeded);
        // The live install was never retired or overwritten…
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("/opt/gitloom.old") && c.Contains("mv"));
        Assert.DoesNotContain(wsl.Calls, c => c.Contains("mv") && c.Contains("/opt/gitloom"));
        // …and the stopped unit is started again (a failed refresh never leaves the daemon down).
        Assert.Equal(new[] { "-d", "GitLoomEnv", "-u", "root", "--", "systemctl", "start", "gitloomd" }, wsl.Calls[^1]);
    }

    // ---- G-12: distro-scoped, never the VM-wide shutdown verb ---------------------------------

    [Fact]
    public void G12_NoRefreshBuilderEmitsTheVmWideShutdownVerb_AndAllAreDistroScoped()
    {
        foreach (var builder in DaemonUpdateCommands.AllBuilders())
        {
            Assert.DoesNotContain("--shutdown", builder);
            // Every refresh command runs inside OUR distro only.
            Assert.Equal("-d", builder[0]);
            Assert.Equal(WslCommands.DistroName, builder[1]);
        }
    }

    // ---- DaemonAutoRefresh: the one startup call ----------------------------------------------

    [Fact]
    public async Task AutoRefresh_DaemonUnreachable_SkipsSilently_WithoutRefreshing()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => throw new InvalidOperationException("connection refused"),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryAttempts: 2,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("unreachable"));
    }

    [Fact]
    public async Task AutoRefresh_UnimplementedAnswer_IsTheSkewSignal_AndRefreshes()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();
        var payload = TempPayloadDir(withFile: true);

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(null), // Unimplemented
            updater,
            payload,
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Equal(new[] { payload }, updater.Refreshes);
        Assert.Contains(log, l => l.Contains("pre-GetDaemonInfo"));
    }

    [Fact]
    public async Task AutoRefresh_MatchingVersions_DoesNothing()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.1.0")),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("up to date"));
    }

    [Fact]
    public async Task AutoRefresh_SkewedButNoShippedPayload_SkipsAndSaysWhy()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.1.0", "")),
            updater,
            payloadDirectory: Path.Combine(Path.GetTempPath(), "gitloom-nonexistent-" + Guid.NewGuid().ToString("N")),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("no daemon payload"));
    }

    [Fact]
    public async Task AutoRefresh_SkewedButEmptyPayloadDir_Skips()
    {
        // An empty dir must never trigger a refresh — staging emptiness would wipe /opt/gitloom.
        var updater = new RecordingUpdater();
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(null),
            updater,
            payloadDirectory: TempPayloadDir(withFile: false),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Empty(updater.Refreshes);
        Assert.Contains(log, l => l.Contains("no daemon payload"));
    }

    [Fact]
    public async Task AutoRefresh_RetriesTheQuery_WhileTheVmBoots_ThenRefreshes()
    {
        var updater = new RecordingUpdater();
        var log = new List<string>();
        var calls = 0;

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => ++calls < 3
                ? throw new InvalidOperationException("still booting")
                : Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.1.0", "0.1.0")),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryAttempts: 5,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Equal(3, calls);
        Assert.Single(updater.Refreshes);
    }

    [Fact]
    public async Task AutoRefresh_AFailedRefresh_IsLogged_NeverThrown()
    {
        var updater = new RecordingUpdater { Outcome = new DaemonRefreshResult(false, "could not stop the gitloomd unit") };
        var log = new List<string>();

        await DaemonAutoRefresh.RunAsync(
            "0.2.0",
            queryDaemonInfo: _ => Task.FromResult<DaemonVersionInfo?>(null),
            updater,
            payloadDirectory: TempPayloadDir(withFile: true),
            log.Add,
            CancellationToken.None,
            queryRetryDelay: TimeSpan.Zero);

        Assert.Contains(log, l => l.Contains("FAILED") && l.Contains("could not stop"));
    }

    private static string TempPayloadDir(bool withFile)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitloom-daemon-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (withFile)
        {
            File.WriteAllText(Path.Combine(dir, "gitloomd"), "stub");
        }

        return dir;
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

    private sealed class RecordingUpdater : IDaemonUpdater
    {
        public List<string> Refreshes { get; } = new();
        public DaemonRefreshResult Outcome { get; set; } = new(true, "refreshed");

        public Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct)
        {
            Refreshes.Add(payloadDirectory);
            return Task.FromResult(Outcome);
        }
    }
}
