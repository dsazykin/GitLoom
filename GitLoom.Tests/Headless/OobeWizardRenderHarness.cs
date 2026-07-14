using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests.Headless;

// P2-48: renders the in-app OOBE wizard (OobeWizardView) offscreen (headless Skia) in ALL FIVE THEMES
// across every phase the state machine produces — diagnostics, the Construct-Sandbox consent + UAC gate,
// reboot-resume, the VM-import checklist, done, the diagnostic hard-stop (blocked), and an actionable
// error card — so the design-system pass (tokens, component classes, icon-per-state encoding, the light
// Daylight Loom theme, the shared MainWindow chrome) can be reviewed without a display. A non-empty-frame
// assertion fails the build on a blank render. PNGs land in the gitignored artifacts_headless/.
public class OobeWizardRenderHarness
{
    [AvaloniaFact]
    public void Capture_OobeWizard_AllThemes()
    {
        try
        {
            foreach (var theme in GitLoom.App.Theming.ThemeManager.Themes)
            {
                GitLoom.App.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                Capture(theme.Key, "diagnostics", new OobeWizardViewModel(OobePhase.Diagnostics, PassingChecks()));
                Capture(theme.Key, "consent", new OobeWizardViewModel(OobePhase.Consent, PassingChecks()));
                Capture(theme.Key, "reboot", new OobeWizardViewModel(OobePhase.Reboot));
                Capture(theme.Key, "importing", new OobeWizardViewModel(OobePhase.Importing, importStages: ImportMix()));
                Capture(theme.Key, "done", new OobeWizardViewModel(OobePhase.Done));
                Capture(theme.Key, "blocked", new OobeWizardViewModel(OobePhase.Blocked, BlockedChecks()));
                Capture(theme.Key, "error", new OobeWizardViewModel(OobePhase.Error,
                    errorMessage: "Step 'First boot (sysctls + Docker)' failed: Docker did not become ready "
                        + "inside GitLoomEnv. If the GitLoomOS payload is missing, reinstall GitLoom; otherwise "
                        + "check the details above and try again — your enabled features and setup progress are preserved."));
            }
        }
        finally
        {
            GitLoom.App.Theming.ThemeManager.Apply(GitLoom.App.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    private static IEnumerable<OobeDiagnosticViewModel> PassingChecks() => new[]
    {
        new OobeDiagnosticViewModel(DiagnosticCheck.Pass("os", "Windows 11 (x64)")),
        new OobeDiagnosticViewModel(DiagnosticCheck.Pass("virt", "Hardware virtualization")),
        new OobeDiagnosticViewModel(DiagnosticCheck.Pass("wsl", "WSL2 platform")),
        new OobeDiagnosticViewModel(DiagnosticCheck.Pass("disk", "Free disk space")),
    };

    private static IEnumerable<OobeDiagnosticViewModel> BlockedChecks() => new[]
    {
        new OobeDiagnosticViewModel(DiagnosticCheck.Pass("os", "Windows 11 (x64)")),
        new OobeDiagnosticViewModel(DiagnosticCheck.Fail("virt", "Hardware virtualization",
            "Hardware virtualization is disabled in your firmware (BIOS/UEFI). Reboot into firmware setup "
            + "and enable Intel VT-x / AMD-V, save, and re-run setup. Nothing has been changed on your machine.",
            SystemDiagnostics.DocVirtualization)),
        new OobeDiagnosticViewModel(DiagnosticCheck.Pass("wsl", "WSL2 platform")),
        new OobeDiagnosticViewModel(DiagnosticCheck.Fail("disk", "Free disk space",
            "GitLoom needs at least 20 GB free on the system drive; only 6.2 GB is available. Free up space and re-run setup.",
            SystemDiagnostics.DocDisk)),
    };

    private static IEnumerable<BootstrapStageViewModel> ImportMix() => new[]
    {
        new BootstrapStageViewModel("Detect WSL2", BootstrapStageState.Done),
        new BootstrapStageViewModel("Import GitLoomEnv", BootstrapStageState.Done, "GitLoomEnv imported."),
        new BootstrapStageViewModel("Configure WSL memory", BootstrapStageState.Done),
        new BootstrapStageViewModel("First boot (sysctls + Docker)", BootstrapStageState.Running,
            "Waiting for Docker to become ready…"),
        new BootstrapStageViewModel("Start gitloomd", BootstrapStageState.Pending),
        new BootstrapStageViewModel("Health-check daemon", BootstrapStageState.Pending),
    };

    private void Capture(string themeKey, string phase, OobeWizardViewModel vm)
    {
        var win = new OobeWizardView { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"oobe_wizard_{themeKey}_{phase}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"oobe wizard {themeKey}/{phase} PNG is empty");

        win.Close();
        Settle();
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
