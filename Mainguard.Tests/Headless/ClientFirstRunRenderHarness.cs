using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.Editions;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// 1d visual proof: render the Client edition's dedicated "Clone" first-run window (the light welcome
// framing around the REUSED Clone Dashboard) so a reviewer can confirm it is the plain-client get-your-
// first-repo surface — clone / open-local / host sign-in — with NONE of the Pro GitLoomOS OOBE chrome.
//
// Same harness discipline as RailEditionRenderHarness: the whole assembly runs non-parallel
// (HeadlessRenderCollection), App.Edition is set to Client and restored in finally, and the window goes
// through HarnessHygiene.Teardown so its ViewModels' work can't leak into a later test.
public class ClientFirstRunRenderHarness
{
    [AvaloniaFact]
    public void Capture_ClientFirstRun()
    {
        var savedEdition = Mainguard.App.Shell.App.Edition;
        try
        {
            // The Client first-run is a client feature — render it under the Client manifest.
            Mainguard.App.Shell.App.Edition = new ClientManifest();
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

            // Construct the surface directly (the App factory wires the window-owner interactions — folder
            // pickers, device-flow dialog, Accounts/SSH — which need no wiring just to render the frame).
            var clone = new CloneDashboardViewModel();
            var vm = new ClientFirstRunViewModel(clone);
            var window = new ClientFirstRunWindow { DataContext = vm, Width = 880, Height = 720 };
            window.Show();
            Settle();

            // It reuses the Clone Dashboard and constructs no control center / agent platform.
            Assert.Same(clone, vm.Clone);
            Assert.Null(Mainguard.App.Shell.App.Edition.CreateControlCenter());

            window.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "client_firstrun.png"));
            HarnessHygiene.Teardown(window);
        }
        finally
        {
            Mainguard.App.Shell.App.Edition = savedEdition;
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
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
