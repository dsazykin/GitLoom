using System;
using System.IO;
using LibGit2Sharp;
using Mainguard.Git.Security;
using Xunit;

namespace Mainguard.Tests;

// T-14: the credentials handler picks SSH key credentials for SSH-form remotes and
// token credentials for HTTP(S) remotes — the single place that decision is made. A
// secret is never concatenated into the URL.
public class CredentialResolverTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-cred-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void Resolve_HttpsWithStoredToken_ReturnsTokenUserPass()
    {
        using var keyDir = new TempDir();
        using var sshDir = new TempDir();
        var keyring = new SecureKeyring(keyDir.Path);
        keyring.SaveSecret(GitHostDetector.TokenKeyForHost("github.com"), "ghp_abc");
        var ssh = new SshKeyService(keyring, sshDir.Path);

        var result = CredentialResolver.Resolve("https://github.com/acme/repo.git", keyring, ssh);

        var creds = Assert.IsType<UsernamePasswordCredentials>(result.Https);
        Assert.Equal("x-access-token", creds.Username);
        Assert.Equal("ghp_abc", creds.Password);
        Assert.Null(result.Ssh);
    }

    [Fact]
    public void Resolve_HttpsNoToken_ReturnsNone()
    {
        using var keyDir = new TempDir();
        using var sshDir = new TempDir();
        var keyring = new SecureKeyring(keyDir.Path);
        var ssh = new SshKeyService(keyring, sshDir.Path);

        var result = CredentialResolver.Resolve("https://github.com/acme/repo.git", keyring, ssh);
        Assert.False(result.HasValue);
    }

    [Fact]
    public void Resolve_SshFormWithKey_ReturnsSshKeyCredentials_WithKeyringPassphrase()
    {
        using var keyDir = new TempDir();
        using var sshDir = new TempDir();
        var keyring = new SecureKeyring(keyDir.Path);
        var ssh = new SshKeyService(keyring, sshDir.Path);

        // Fake an id_ed25519 pair on disk + a stored passphrase (no ssh-keygen needed).
        var priv = Path.Combine(sshDir.Path, "id_ed25519");
        File.WriteAllText(priv, "-----BEGIN OPENSSH PRIVATE KEY-----\nfake\n-----END OPENSSH PRIVATE KEY-----\n");
        File.WriteAllText(priv + ".pub", "ssh-ed25519 AAAA... me@host\n");
        keyring.SaveSecret(SshKeyService.PassphraseKey(priv), "keypass");

        var result = CredentialResolver.Resolve("git@github.com:acme/repo.git", keyring, ssh);

        Assert.NotNull(result.Ssh);
        Assert.Null(result.Https);
        Assert.Equal("git", result.Ssh!.Username);
        Assert.Equal(priv, result.Ssh.PrivateKeyPath);
        Assert.Equal(priv + ".pub", result.Ssh.PublicKeyPath);
        Assert.Equal("keypass", result.Ssh.Passphrase); // pulled from keyring, never the URL
    }
}
