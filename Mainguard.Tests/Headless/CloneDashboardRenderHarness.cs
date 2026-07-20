using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.Git.Services;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// P2-48 — renders the multi-provider Clone Dashboard offscreen (headless Skia) so the generalized
/// screen can be visually reviewed: the provider segmented selector (GitHub / GitLab) plus a populated
/// repo grid, with GitHub selected and GitLab selected, across all FIVE themes. PNGs land in
/// artifacts_headless/ — visual review, not pass/fail (frame-non-empty is asserted).
/// </summary>
public class CloneDashboardRenderHarness
{
    // Canned lister: both hosts "signed in", each returns a small host-flavored repo set. No network.
    private sealed class CannedRepoService : IHostRepositoryService
    {
        public bool IsSupported(string host, HostKind kind) =>
            kind == HostKind.GitHub || kind == HostKind.GitLab;

        public Task<IReadOnlyList<RemoteRepository>> ListMyRepositoriesAsync(string host, HostKind kind, CancellationToken ct)
        {
            IReadOnlyList<RemoteRepository> repos = kind == HostKind.GitLab
                ? new List<RemoteRepository>
                {
                    new() { Kind = kind, Host = host, Name = "webapp", FullName = "acme/webapp", IsPrivate = true,
                            Description = "The customer-facing web app.", CloneUrl = "https://gitlab.com/acme/webapp.git", UpdatedAt = "2026-06-10T12:00:00Z" },
                    new() { Kind = kind, Host = host, Name = "infra", FullName = "acme/infra", IsPrivate = true,
                            Description = "Terraform + CI pipelines.", CloneUrl = "https://gitlab.com/acme/infra.git", UpdatedAt = "2026-06-09T12:00:00Z" },
                    new() { Kind = kind, Host = host, Name = "docs", FullName = "acme/docs", IsPrivate = false,
                            Description = "Public documentation site.", CloneUrl = "https://gitlab.com/acme/docs.git", UpdatedAt = "2026-06-01T12:00:00Z" },
                }
                : new List<RemoteRepository>
                {
                    new() { Kind = kind, Host = host, Name = "hello-world", FullName = "octocat/hello-world", IsPrivate = false,
                            Description = "My first repository on GitHub.", CloneUrl = "https://github.com/octocat/hello-world.git", UpdatedAt = "2026-06-11T12:00:00Z" },
                    new() { Kind = kind, Host = host, Name = "spoon-knife", FullName = "octocat/spoon-knife", IsPrivate = false,
                            Description = "This repo is for demonstration purposes only.", CloneUrl = "https://github.com/octocat/spoon-knife.git", UpdatedAt = "2026-06-08T12:00:00Z" },
                    new() { Kind = kind, Host = host, Name = "vault", FullName = "octocat/vault", IsPrivate = true,
                            Description = "Private notes.", CloneUrl = "https://github.com/octocat/vault.git", UpdatedAt = "2026-05-20T12:00:00Z" },
                };
            return Task.FromResult(repos);
        }
    }

    [AvaloniaFact]
    public void Capture_CloneDashboard_BothProviders_AllThemes()
    {
        try
        {
            foreach (var theme in ThemeManager.Themes)
            {
                ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                using var dir = new TempDir();
                var keyring = new SecureKeyring(dir.Path);
                // Sign into both hosts so the segmented selector is shown.
                keyring.SaveSecret(GitHostDetector.TokenKeyForHost("github.com"), "ghp_demo");
                keyring.SaveSecret(GitHostDetector.TokenKeyForHost("gitlab.com"), "glpat_demo");

                // GitHub selected (default first provider).
                var ghVm = new CloneDashboardViewModel(keyring, new CannedRepoService());
                RenderProvider(ghVm, HostKind.GitHub, $"clone_dashboard_github_{theme.Key}.png");

                // GitLab selected.
                var glVm = new CloneDashboardViewModel(keyring, new CannedRepoService());
                SelectKind(glVm, HostKind.GitLab);
                RenderProvider(glVm, HostKind.GitLab, $"clone_dashboard_gitlab_{theme.Key}.png");
            }
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    private static void SelectKind(CloneDashboardViewModel vm, HostKind kind)
    {
        foreach (var p in vm.Providers)
            if (p.Kind == kind) { vm.SelectProviderCommand.Execute(p); break; }
    }

    private static void RenderProvider(CloneDashboardViewModel vm, HostKind expected, string fileName)
    {
        var win = new Window
        {
            Width = 900,
            Height = 620,
            Content = new CloneDashboardView { DataContext = vm },
            DataContext = vm,
        };
        win.Show();
        Settle();

        Assert.True(vm.IsAuthenticated);
        Assert.True(vm.HasProviderSelector);
        Assert.Equal(expected, vm.SelectedProvider?.Kind);

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), fileName);
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"{fileName} PNG is empty");

        HarnessHygiene.Teardown(win);
        Settle();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-clone-render-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
