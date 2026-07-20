using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests.Headless;

/// <summary>
/// Renders the T-21 UI offscreen (headless Skia) for visual review: the Git Profiles window (a couple of
/// rows + the editor) and the Clone Dashboard's clone-progress overlay at a mid-fill percentage. PNGs are
/// saved to artifacts_headless/ — visual review, not pass/fail.
/// </summary>
public class ProfilesCloneRenderHarness
{
    private sealed class CannedProfileService : IProfileService
    {
        private readonly List<GitProfile> _store;
        public CannedProfileService(IEnumerable<GitProfile> seed) => _store = seed.ToList();
        public IReadOnlyList<GitProfile> GetProfiles() => _store;
        public GitProfile? GetProfile(int id) => _store.FirstOrDefault(p => p.Id == id);
        public GitProfile Create(GitProfile profile) { _store.Add(profile); return profile; }
        public void Update(GitProfile profile) { }
        public GitProfile? Delete(int id) => null;
        public void Restore(GitProfile profile) { }
        public void Apply(string repoPath, GitProfile profile) { }
    }

    [AvaloniaFact]
    public void Capture_ProfilesWindow_WithRows()
    {
        var svc = new CannedProfileService(new[]
        {
            new GitProfile { Id = 1, Name = "Work", UserName = "Grace Hopper", UserEmail = "grace@navy.mil", SignCommits = true, SigningKey = "ABC123" },
            new GitProfile { Id = 2, Name = "Open Source", UserName = "Ada Lovelace", UserEmail = "ada@oss.dev" },
        });

        var vm = new ProfilesViewModel(svc, repoPath: "/tmp/demo-repo");
        var win = new ProfilesWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "profiles_window.png"));

        Assert.Equal(2, vm.Profiles.Count);
        Assert.True(vm.HasRepo);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_CloneProgress_Overlay()
    {
        var vm = new CloneDashboardViewModel
        {
            IsCloning = true,
            CloneProgressPercent = 63,
            CloneStatusText = "Receiving objects 189/300",
        };

        var win = new Window
        {
            Width = 720,
            Height = 520,
            Content = new CloneDashboardView { DataContext = vm },
            DataContext = vm,
        };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "clone_progress.png"));

        Assert.True(vm.IsCloning);
        Assert.Equal(63, vm.CloneProgressPercent);
        HarnessHygiene.Teardown(win);
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
