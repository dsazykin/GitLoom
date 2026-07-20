using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

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
            foreach (var theme in Mainguard.UI.Theming.ThemeManager.Themes)
            {
                Mainguard.UI.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                Capture(theme.Key, "diagnostics", new OobeWizardViewModel(OobePhase.Diagnostics, PassingChecks()));
                Capture(theme.Key, "consent", new OobeWizardViewModel(OobePhase.Consent, PassingChecks()));
                Capture(theme.Key, "reboot", new OobeWizardViewModel(OobePhase.Reboot));
                Capture(theme.Key, "importing", new OobeWizardViewModel(OobePhase.Importing, importStages: ImportMix()));
                // The agent-CLI picker (P2-22 §J-5): the fresh multi-select, the mid-install state
                // (per-row progress + Cancel), and the mixed terminal state (installed + an actionable
                // per-row failure + Continue) — every state the step can show.
                Capture(theme.Key, "clis_pick", new OobeWizardViewModel(OobePhase.AgentClis, cliOptions: CliPickMix()));
                Capture(theme.Key, "clis_installing", new OobeWizardViewModel(OobePhase.AgentClis,
                    cliOptions: CliInstallingMix(), isInstallingClis: true));
                Capture(theme.Key, "clis_results", new OobeWizardViewModel(OobePhase.AgentClis, cliOptions: CliResultMix()));
                // The repo-onboarding step (PR2): the two-choice entry, the scanning line, the
                // default-checked results list, the mid-copy state (per-row progress + Cancel), and
                // the mixed terminal state (copied + an actionable per-row failure + Continue) —
                // every state the step can show.
                Capture(theme.Key, "repos_choice", new OobeWizardViewModel(OobePhase.RepoOnboarding));
                Capture(theme.Key, "repos_scanning", new OobeWizardViewModel(OobePhase.RepoOnboarding, isRepoScanning: true));
                Capture(theme.Key, "repos_results", new OobeWizardViewModel(OobePhase.RepoOnboarding, repoRows: RepoPickMix()));
                Capture(theme.Key, "repos_copying", new OobeWizardViewModel(OobePhase.RepoOnboarding,
                    repoRows: RepoCopyingMix(), isProvisioningRepos: true));
                Capture(theme.Key, "repos_failure", new OobeWizardViewModel(OobePhase.RepoOnboarding, repoRows: RepoResultMix()));
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
            Mainguard.UI.Theming.ThemeManager.Apply(Mainguard.UI.Theming.ThemeManager.DefaultKey, persist: false);
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

    // Representative rows from the real starter channel (ids/versions match adapters.starter.json),
    // plus a deliberately long display name + long version to prove truncation never breaks the row.
    private static IEnumerable<AgentCliRowViewModel> CliPickMix() => new[]
    {
        new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210") { IsSelected = true },
        new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4"),
        new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1") { IsSelected = true },
        new AgentCliRowViewModel("long", "An Agent CLI With A Deliberately Very Long Product Name That Truncates", "10.20.300-rc.4+build.99"),
    };

    private static IEnumerable<AgentCliRowViewModel> CliInstallingMix() => new[]
    {
        new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
        new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4")
        {
            IsInstalling = true,
            StatusMessage = "Downloading, verifying, and installing — this can take a few minutes on a slow connection.",
        },
        new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1") { IsSelected = true },
    };

    private static IEnumerable<AgentCliRowViewModel> CliResultMix() => new[]
    {
        new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
        new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4")
        {
            IsFailed = true,
            IsSelected = true,
            StatusMessage = "codex was not installed: the downloaded file did not match GitLoom's published "
                + "checksum, so it was refused. This usually means the download was corrupted or intercepted — "
                + "check your network (proxy/VPN) and try again.",
        },
        new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
    };

    // Representative discovered-repo rows, including a deliberately deep path to prove the mono
    // path line truncates instead of breaking the card.
    private static IEnumerable<OnboardRepoRowViewModel> RepoPickMix() => new[]
    {
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\gitloom"),
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\client-work\mergeloom-site"),
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\experiments\a-deliberately-long-repository-folder-name-that-truncates\nested\deep"),
    };

    private static IEnumerable<OnboardRepoRowViewModel> RepoCopyingMix() => new[]
    {
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\gitloom", isOnboarded: true),
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\client-work\mergeloom-site")
        {
            IsProvisioning = true,
            StatusMessage = "Copying into GitLoom OS — a large repository can take a few minutes.",
        },
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\experiments\side-project"),
    };

    private static IEnumerable<OnboardRepoRowViewModel> RepoResultMix() => new[]
    {
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\gitloom", isOnboarded: true),
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\client-work\mergeloom-site")
        {
            IsFailed = true,
            IsSelected = true,
            StatusMessage = "This repository was not copied: the GitLoom OS daemon reported Unavailable. "
                + "The others continue — you can retry it here, or skip: GitLoom copies it automatically "
                + "the first time you open it.",
        },
        new OnboardRepoRowViewModel(@"C:\Users\dev\code\experiments\side-project") { IsSelected = false },
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

        HarnessHygiene.Teardown(win);
        Settle();
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
