using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Security;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-14 Accounts + SSH-keys preferences pages offscreen (headless Skia) so
// their layout/theme can be inspected without a display. Captures PNGs to
// artifacts_headless/ and asserts the rendered frame is non-empty.
public class AccountsSshRenderHarness
{
    [AvaloniaFact]
    public void Capture_AccountsWindow()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret(GitLoom.Core.Security.GitHostDetector.TokenKeyForHost("github.com"), "ghp_demo");

        var vm = new AccountsViewModel(keyring);
        // Reveal a PAT paste field so the render exercises that state too.
        foreach (var row in vm.Accounts)
            if (row.Host == "bitbucket.org") row.IsPatEntryVisible = true;

        var win = new AccountsWindow { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), "accounts_window.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, "accounts PNG is empty");
    }

    [AvaloniaFact]
    public void Capture_SshKeysWindow()
    {
        using var sshDir = new TempDir();
        using var keyDir = new TempDir();
        // Seed a couple of fake key pairs so the list is populated (no ssh-keygen needed).
        File.WriteAllText(Path.Combine(sshDir.Path, "id_ed25519"), "priv\n");
        File.WriteAllText(Path.Combine(sshDir.Path, "id_ed25519.pub"), "ssh-ed25519 AAAAC3Nz... me@laptop\n");
        File.WriteAllText(Path.Combine(sshDir.Path, "id_rsa"), "priv\n");
        File.WriteAllText(Path.Combine(sshDir.Path, "id_rsa.pub"), "ssh-rsa AAAAB3Nz... me@desktop\n");

        var svc = new SshKeyService(new SecureKeyring(keyDir.Path), sshDir.Path);
        var vm = new SshKeysViewModel(svc);

        var win = new SshKeysWindow { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), "ssh_keys_window.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, "ssh keys PNG is empty");
        Assert.Equal(2, vm.Keys.Count);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-render-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
