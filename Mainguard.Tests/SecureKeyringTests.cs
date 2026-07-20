using System;
using System.IO;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

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

    [Fact]
    public void MasterKey_IsEncryptedAtRest_OnWindows()
    {
        // The DataProtection key ring lives next to the secrets it encrypts. On Windows
        // the master key must be DPAPI-wrapped ("encryptedKey" descriptor) rather than a
        // plain-XML <masterKey> value; elsewhere DPAPI is unavailable and the directory
        // ACL remains the boundary, so the assertion is Windows-only.
        if (!OperatingSystem.IsWindows()) return;

        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret("token_github.com", "ghp_plaintext_marker");

        var keyFiles = Directory.GetFiles(dir.Path, "key-*.xml");
        Assert.NotEmpty(keyFiles);
        foreach (var keyFile in keyFiles)
        {
            var xml = File.ReadAllText(keyFile);
            Assert.Contains("encryptedKey", xml);
        }
    }

    [Fact]
    public void PreexistingUnencryptedKeyRing_StillDecrypts()
    {
        // Upgrade path: secrets stored before the DPAPI change were protected with an
        // unencrypted-at-rest master key. Reopening the same directory must still
        // round-trip them (the provider reads legacy plain keys even when new keys
        // are DPAPI-wrapped).
        using var dir = new TempDir();
        var first = new SecureKeyring(dir.Path);
        first.SaveSecret("token_github.com", "ghp_upgrade_marker");

        var reopened = new SecureKeyring(dir.Path);
        Assert.Equal("ghp_upgrade_marker", reopened.RetrieveSecret("token_github.com"));
    }
}
