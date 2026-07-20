using System;
using System.IO;
using Mainguard.Git;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Phase-4 persisted-id migration: verifies the OS-agnostic core of the Windows data-root move
/// (<c>%LocalAppData%\GitLoom</c> → <c>\Mainguard</c>). The public entry point is Windows-gated and
/// resolves <c>%LocalAppData%</c>; this exercises the move POLICY on any platform via the internal
/// <see cref="MainguardPaths.TryMigrateDataRoot"/> (visible through InternalsVisibleTo).
/// </summary>
public class MainguardPathsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mg-migrate-" + Guid.NewGuid().ToString("N"));

    private string Legacy => Path.Combine(_root, "GitLoom");
    private string Current => Path.Combine(_root, "Mainguard");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Moves_legacy_to_current_when_current_absent_preserving_contents()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "daemon.token"), "secret");
        Directory.CreateDirectory(Path.Combine(Legacy, "Keyring"));
        File.WriteAllText(Path.Combine(Legacy, "Keyring", "token_github.keyring"), "cipher");

        var moved = MainguardPaths.TryMigrateDataRoot(Legacy, Current, log: null);

        Assert.True(moved);
        Assert.False(Directory.Exists(Legacy));
        Assert.True(Directory.Exists(Current));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(Current, "daemon.token")));
        Assert.Equal("cipher", File.ReadAllText(Path.Combine(Current, "Keyring", "token_github.keyring")));
    }

    [Fact]
    public void Is_noop_when_current_already_exists()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "old.txt"), "old");
        Directory.CreateDirectory(Current);
        File.WriteAllText(Path.Combine(Current, "new.txt"), "new");

        var moved = MainguardPaths.TryMigrateDataRoot(Legacy, Current, log: null);

        // Never merge/overwrite an existing new root; both dirs are left exactly as they were.
        Assert.False(moved);
        Assert.True(File.Exists(Path.Combine(Legacy, "old.txt")));
        Assert.True(File.Exists(Path.Combine(Current, "new.txt")));
        Assert.False(File.Exists(Path.Combine(Current, "old.txt")));
    }

    [Fact]
    public void Is_noop_on_fresh_install_when_neither_exists()
    {
        var moved = MainguardPaths.TryMigrateDataRoot(Legacy, Current, log: null);

        Assert.False(moved);
        Assert.False(Directory.Exists(Current));
    }

    [Fact]
    public void Is_idempotent_second_call_does_nothing()
    {
        Directory.CreateDirectory(Legacy);
        File.WriteAllText(Path.Combine(Legacy, "x"), "1");

        Assert.True(MainguardPaths.TryMigrateDataRoot(Legacy, Current, log: null));
        var again = MainguardPaths.TryMigrateDataRoot(Legacy, Current, log: null);

        Assert.False(again);
        Assert.True(Directory.Exists(Current));
    }
}
