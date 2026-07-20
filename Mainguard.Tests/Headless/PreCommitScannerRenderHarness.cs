using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Safety;
using Mainguard.Git.Services;
using Mainguard.Tests.Fakes;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-30 pre-commit findings panel offscreen (headless Skia) in its key states so the
// grouped severity layout, kind icons, path:line locations, reveal affordances, the blocker banner
// with "Commit anyway", and the all-clear state can be inspected without a display. The findings are
// set directly (no scan) via a canned VM. Captures a PNG per state to artifacts_headless/ (visual
// review, not pass/fail).
public class PreCommitScannerRenderHarness
{
    private static PreCommitFindingsViewModel CannedVm()
        => new(new PreCommitScanner(new FakeGitService()), "/repo");

    [AvaloniaFact]
    public void Capture_FindingsPanel_BlockerWarningInfo()
    {
        var vm = CannedVm();
        vm.SetFindings(new[]
        {
            new PreCommitFinding { Kind = FindingKind.Secret, Severity = FindingSeverity.Blocker, Path = "src/config.py", Line = 42, Message = "Possible AWS access key id committed", Rule = "aws-access-key-id" },
            new PreCommitFinding { Kind = FindingKind.MergeMarker, Severity = FindingSeverity.Blocker, Path = "app/main.cs", Line = 118, Message = "Unresolved merge conflict marker", Rule = "merge-marker" },
            new PreCommitFinding { Kind = FindingKind.LargeFile, Severity = FindingSeverity.Warning, Path = "assets/video.mp4", Message = "Large file (18.4 MB exceeds 5 MB limit)", Rule = "large-file" },
            new PreCommitFinding { Kind = FindingKind.ManyFiles, Severity = FindingSeverity.Info, Path = "", Message = "142 files staged (over 100)", Rule = "many-files" },
        });
        vm.AwaitingOverride = true; // show the blocker banner too

        var win = Host(vm);
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "precommit_findings.png"));

        Assert.True(vm.HasBlockers);
        Assert.Equal(3, vm.Groups.Count);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_FindingsPanel_AllClear()
    {
        var vm = CannedVm();
        vm.SetFindings(Array.Empty<PreCommitFinding>());

        var win = Host(vm);
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "precommit_allclear.png"));

        Assert.True(vm.IsAllClear);
        HarnessHygiene.Teardown(win);
    }

    private static Window Host(PreCommitFindingsViewModel vm)
    {
        var panel = new PreCommitFindingsView { DataContext = vm };
        var root = new Border { Padding = new Thickness(16), Child = panel };
        if (Application.Current!.TryGetResource("SurfaceWindow", Application.Current.ActualThemeVariant, out var bg)
            && bg is IBrush brush)
        {
            root.Background = brush;
        }
        return new Window { Width = 460, Height = 470, Content = root };
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
