using System;
using System.IO;
using Mainguard.Server.Gateway;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// The daemon-DB bootstrap must never keep the gRPC surface from binding (the class doc's
/// contract). Field outage 2026-07-17: a WSL idle-stop killed the daemon mid-migration, orphaning
/// EF's <c>__EFMigrationsLock</c> row; every subsequent boot hung forever inside
/// <c>Migrate()</c> → <c>AcquireDatabaseLock()</c> and Kestrel never bound. TryPrepareDatabase now
/// clears the stale lock (the daemon is the DB's only writer) and runs the migration under a
/// watchdog that falls back to the in-memory stores.
/// </summary>
public class DatabaseBootstrapTests
{
    [Fact]
    public void FreshDatabase_Prepares_AndReturnsFactory()
    {
        var dir = TempDir();
        try
        {
            var ok = GatewayServiceRegistration.TryPrepareDatabase(
                Path.Combine(dir, "daemon.db"), out var factory);

            Assert.True(ok);
            using var db = factory();
            Assert.NotNull(db);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void StaleMigrationLock_FromADaemonKilledMidMigration_IsClearedAndBootSucceeds()
    {
        var dir = TempDir();
        try
        {
            var dbPath = Path.Combine(dir, "daemon.db");

            // A fully migrated DB whose previous owner died holding the migration lock.
            Assert.True(GatewayServiceRegistration.TryPrepareDatabase(dbPath, out _));
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText =
                    "CREATE TABLE IF NOT EXISTS \"__EFMigrationsLock\" (" +
                    "\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, " +
                    "\"Timestamp\" TEXT NOT NULL); " +
                    "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") " +
                    "VALUES (1, '2026-01-01T00:00:00.0000000Z');";
                command.ExecuteNonQuery();
            }

            // Without the stale-lock clear this waits on the dead holder until the watchdog trips
            // (a bounded regression signal, not a hung test run).
            var ok = GatewayServiceRegistration.TryPrepareDatabase(
                dbPath, out var factory, watchdog: TimeSpan.FromSeconds(15));

            Assert.True(ok);
            Assert.NotNull(factory);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void WatchdogExpiry_FallsBackToInMemory_InsteadOfBlockingBind()
    {
        var dir = TempDir();
        try
        {
            // A zero watchdog cannot be satisfied by any real migration — the method must give up
            // and report failure (the registration then wires the in-memory stores) rather than wait.
            var ok = GatewayServiceRegistration.TryPrepareDatabase(
                Path.Combine(dir, "daemon.db"), out _, watchdog: TimeSpan.Zero);

            Assert.False(ok);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitloom-dbboot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Pooled SQLite connections hold the file open after dispose; drop the pools first.
    /// The zero-watchdog test's migration may also still be finishing in the background — retry.</summary>
    private static void DeleteDir(string dir)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 50)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
    }
}
