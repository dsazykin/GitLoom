using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Editions;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Git.Models;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// 1f twin full-shell render guard (ADR-0001): render the ENTIRE MainWindow shell — top-nav toolbar +
// section rail + an open workspace — under BOTH editions → artifacts_headless/mainwindow_pro.png and
// mainwindow_client.png, so a reviewer can eyeball the two shipping shapes side by side. This is the
// "did the Client grow an agent kill switch / did Pro lose the Coordinator" screenshot guard: the
// full-shell companion to RailEditionRenderHarness's rail-only pair (which this file was seeded from).
//
// Same harness discipline as the other edition harnesses: the whole assembly runs non-parallel
// (HeadlessRenderCollection), so mutating the static App.Edition / App.OrchestratorServicesFactory here
// cannot race another test; BOTH are restored in a finally, and every window goes through
// HarnessHygiene.Teardown so a VM's timers / fire-and-forget loads can't leak into a later test. Pro
// injects the scripted MockOrchestrator behind the shipped factory seam exactly as the design harnesses
// do so the coordinator/agent chrome paints without a live daemon; the Client shell builds no control
// center at all.
public class MainWindowEditionRenderHarness
{
    [AvaloniaFact]
    public void Capture_MainWindow_UnderBothEditions()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo repo\n", "chore: seed");
        fx.CommitFile("src/app.cs", "class App { }\n", "feat: app");
        fx.CreateBranch("feature/work");

        var savedEdition = GitLoom.App.App.Edition;
        var savedFactory = GitLoom.App.App.OrchestratorServicesFactory;
        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            // ---- Pro (shipped default): the full agent platform, with the scripted mock behind the shipped
            // orchestrator-services seam so the Coordinator destination + agent rail chrome paint. ----
            GitLoom.App.App.Edition = EditionManifests.Pro;
            GitLoom.App.App.OrchestratorServicesFactory =
                () => OrchestratorServices.FromSingle(new MockOrchestrator());
            CaptureShell(fx, "mainwindow_pro.png", expectControlCenter: true);

            // ---- Client: the plain Git GUI. CreateControlCenter() returns null → no agent rail, no
            // Coordinator/Resources destinations, no kill switch. ----
            GitLoom.App.App.Edition = EditionManifests.Client;
            CaptureShell(fx, "mainwindow_client.png", expectControlCenter: false);
        }
        finally
        {
            GitLoom.App.App.Edition = savedEdition;
            GitLoom.App.App.OrchestratorServicesFactory = savedFactory;
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    // Build the real shell for the currently-selected edition, open the fixture repo so the whole shell
    // (toolbar + rail + workspace) is painted, capture the frame, and tear the window down. The
    // control-center assert makes the two captures provably different shapes — Pro composes a control
    // center, Client composes none — so a regression that let a kill switch into the Client (or dropped
    // the Coordinator from Pro) fails here, not just visually.
    private static void CaptureShell(TempRepoFixture fx, string fileName, bool expectControlCenter)
    {
        var vm = new MainWindowViewModel();
        var win = new MainWindow { DataContext = vm, Width = 1420, Height = 920 };
        win.Show();

        vm.OpenRepository(new Repository { Path = fx.RepoPath, DisplayName = "demo" });
        for (int i = 0; i < 200 && vm.CurrentWorkspace == null; i++) Pump();
        for (int i = 0; i < 80; i++) Pump();

        Assert.NotNull(vm.CurrentWorkspace);
        if (expectControlCenter) Assert.NotNull(vm.ControlCenter);
        else Assert.Null(vm.ControlCenter);

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), fileName));
        HarnessHygiene.Teardown(win);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(25);
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
