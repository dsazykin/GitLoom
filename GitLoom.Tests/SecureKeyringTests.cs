using System;
using System.IO;
using GitLoom.Core.Security;
using Xunit;

namespace GitLoom.Tests;

// TI-14 #4: keyring round-trip + graceful null on a corrupt payload, using the T-14
// storage-directory override so tests never touch the real user keyring.
public class SecureKeyringTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-keyring-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void SaveRetrieveDelete_RoundTrip()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);

        keyring.SaveSecret("token_github.com", "ghp_secretvalue");
        Assert.Equal("ghp_secretvalue", keyring.RetrieveSecret("token_github.com"));

        keyring.DeleteSecret("token_github.com");
        Assert.Null(keyring.RetrieveSecret("token_github.com"));
    }

    [Fact]
    public void Retrieve_ShouldReturnNull_OnMissingKey()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        Assert.Null(keyring.RetrieveSecret("nope"));
    }

    [Fact]
    public void Retrieve_ShouldReturnNull_OnCorruptPayload()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);

        // Write garbage directly to the backing .keyring file (bypassing Protect).
        File.WriteAllText(Path.Combine(dir.Path, "token_github.com.keyring"), "this-is-not-a-valid-protected-payload");

        // Must NOT throw — a corrupt/foreign payload decrypts to null.
        Assert.Null(keyring.RetrieveSecret("token_github.com"));
    }

    [Fact]
    public void SecretText_IsNotStoredInPlaintext_OnDisk()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret("token_github.com", "ghp_plaintext_marker");

        var onDisk = File.ReadAllText(Path.Combine(dir.Path, "token_github.com.keyring"));
        Assert.DoesNotContain("ghp_plaintext_marker", onDisk); // DataProtection-encrypted at rest
    }
}
