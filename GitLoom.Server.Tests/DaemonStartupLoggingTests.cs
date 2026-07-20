using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Agents;
using GitLoom.Protos.V1;
using GitLoom.Server.Gateway;
using GitLoom.Server.Logging;
using GitLoom.Server.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

using Mainguard.Git;
namespace GitLoom.Server.Tests;

/// <summary>
/// The startup + migration milestones the daemon logging pipeline adds: the #194 lock-hang is now
/// diagnosable from the Migration milestones ("preparing db / stale lock cleared / migrate ok /
/// watchdog fired"), and the Lifecycle "bound" milestone confirms the host reached ready. The
/// migration paths are driven through the static <c>TryPrepareDatabase</c> directly (the log delegate
/// makes them observable without a host); the bound milestone is read off the in-proc host's captured
/// logs.
/// </summary>
public sealed class DaemonStartupLoggingTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public DaemonStartupLoggingTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public void Migration_Milestones_AreLogged_IncludingStaleLockClear()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "gw.db");
        try
        {
            using (var db = new AppDbContext(dbPath))
            {
                db.Database.Migrate();
            }

            // Simulate a daemon that died mid-migration: an orphaned __EFMigrationsLock row that EF would
            // otherwise wait on forever. The table may not exist on SQLite, so create-if-absent first.
            using (var db = new AppDbContext(dbPath))
            {
                db.Database.ExecuteSqlRaw(
                    "CREATE TABLE IF NOT EXISTS \"__EFMigrationsLock\" "
                    + "(\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, \"Timestamp\" TEXT NOT NULL);");
                db.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2020-01-01T00:00:00');");
            }

            var logs = new List<string>();
            var ok = GatewayServiceRegistration.TryPrepareDatabase(dbPath, out _, log: logs.Add);

            Assert.True(ok);
            Assert.Contains(logs, l => l.Contains("preparing db", StringComparison.Ordinal));
            Assert.Contains(logs, l => l.Contains("stale migration lock cleared", StringComparison.Ordinal));
            Assert.Contains(logs, l => l.Contains("migrate ok", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Migration_WatchdogFallback_IsLogged()
    {
        var dir = NewTempDir();
        var dbPath = Path.Combine(dir, "gw.db");
        try
        {
            var logs = new List<string>();
            // A zero watchdog forces migrate.Wait(...) to time out before the migration task completes —
            // exactly the in-memory-fallback path the #194 hang takes, made observable.
            var ok = GatewayServiceRegistration.TryPrepareDatabase(
                dbPath, out _, watchdog: TimeSpan.Zero, log: logs.Add);

            Assert.False(ok);
            Assert.Contains(logs, l => l.Contains("watchdog fired", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task Lifecycle_BoundEndpoint_IsLogged()
    {
        // Force the in-proc host fully started (a real authenticated RPC), so ApplicationStarted has fired.
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        await client.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());

        for (var i = 0; i < 50 && !_daemon.CapturedLogs.Any(IsBoundLine); i++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(_daemon.CapturedLogs, IsBoundLine);

        static bool IsBoundLine(string line) =>
            line.Contains("[" + DaemonLogCategories.Lifecycle + "]", StringComparison.Ordinal)
            && line.Contains("bound 127.0.0.1", StringComparison.Ordinal);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gl-startuplog-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}
