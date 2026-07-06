using System;
using System.IO;
using System.Linq;
using GitLoom.Core.Security;
using Xunit;

namespace GitLoom.Tests;

// TI-14 #3: ssh-keygen must be invoked via ProcessStartInfo.ArgumentList (never a shell
// string) so a passphrase can never be word-split / glob-expanded / leaked to shell
// history. The argv-construction test asserts the exact ArgumentList without spawning a
// process; the round-trip test does a REAL local ssh-keygen (present via Git for Windows).
public class SshKeyServiceTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-ssh-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void SshKeygen_ArgConstruction_ShouldUseArgumentList_NeverShellString()
    {
        var psi = SshKeyService.BuildKeygenStartInfo("/tmp/keys/id_ed25519", "s3cret pass", "me@host");

        Assert.Equal("ssh-keygen", psi.FileName);
        Assert.False(psi.UseShellExecute);
        // Arguments (the shell-string surface) must be empty — every token is a discrete
        // ArgumentList element, including the passphrase.
        Assert.True(string.IsNullOrEmpty(psi.Arguments));
        Assert.Equal(
            new[] { "-t", "ed25519", "-f", "/tmp/keys/id_ed25519", "-N", "s3cret pass", "-C", "me@host" },
            psi.ArgumentList.ToArray());
    }

    [Fact]
    public void SshKeygen_ArgConstruction_EmptyPassphrase_StillDiscreteArg()
    {
        var psi = SshKeyService.BuildKeygenStartInfo("/tmp/id_ed25519", "", null);
        Assert.Equal(new[] { "-t", "ed25519", "-f", "/tmp/id_ed25519", "-N", "" }, psi.ArgumentList.ToArray());
    }

    [Theory]
    [InlineData("git@github.com:acme/repo.git", true)]
    [InlineData("ssh://git@github.com/acme/repo.git", true)]
    [InlineData("https://github.com/acme/repo.git", false)]
    [InlineData(@"C:\repo", false)]
    [InlineData("", false)]
    public void IsSshRemote_ClassifiesRemoteForm(string url, bool expected)
        => Assert.Equal(expected, SshKeyService.IsSshRemote(url));

    [Fact]
    public void PassphraseKey_IsFileSystemSafe_AndPrefixed()
    {
        var key = SshKeyService.PassphraseKey(@"C:\Users\me\.ssh\id_ed25519");
        Assert.StartsWith("sshpass_", key);
        Assert.DoesNotContain(':', key);
        Assert.DoesNotContain('\\', key);
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void Generate_RealKeygen_RoundTripsKeyAndPassphrase()
    {
        if (!SshKeyService.IsSshKeygenAvailable())
            return; // ssh-keygen not on PATH in this environment — covered by the local matrix.

        using var sshDir = new TempDir();
        using var keyringDir = new TempDir();
        var keyring = new SecureKeyring(keyringDir.Path);
        var svc = new SshKeyService(keyring, sshDir.Path);

        var keyPath = Path.Combine(sshDir.Path, "id_ed25519");
        const string passphrase = "correct horse battery";

        var generated = svc.Generate(keyPath, passphrase, "gitloom-test@local");

        // 1. Key files exist on disk.
        Assert.True(File.Exists(keyPath), "private key file missing");
        Assert.True(File.Exists(keyPath + ".pub"), "public key file missing");
        Assert.Equal("ssh-ed25519", generated.KeyType);
        Assert.StartsWith("ssh-ed25519", generated.PublicKeyText);

        // 2. ListKeys finds the generated pair.
        var listed = svc.ListKeys();
        Assert.Contains(listed, k => k.Name == "id_ed25519");

        // 3. Passphrase round-trips through the keyring under sshpass_<sanitized-path>.
        Assert.Equal(passphrase, svc.GetPassphrase(keyPath));
        Assert.Equal(passphrase, keyring.RetrieveSecret(SshKeyService.PassphraseKey(keyPath)));

        // 4. SECURITY: the passphrase must never appear in either key file on disk.
        Assert.DoesNotContain(passphrase, File.ReadAllText(keyPath + ".pub"));
        Assert.DoesNotContain(passphrase, File.ReadAllText(keyPath));
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void Generate_NoPassphrase_StoresNothingInKeyring()
    {
        if (!SshKeyService.IsSshKeygenAvailable()) return;

        using var sshDir = new TempDir();
        using var keyringDir = new TempDir();
        var svc = new SshKeyService(new SecureKeyring(keyringDir.Path), sshDir.Path);
        var keyPath = Path.Combine(sshDir.Path, "id_ed25519");

        svc.Generate(keyPath, "", null);

        Assert.True(File.Exists(keyPath + ".pub"));
        Assert.Null(svc.GetPassphrase(keyPath)); // unencrypted key: no keyring entry
    }
}
