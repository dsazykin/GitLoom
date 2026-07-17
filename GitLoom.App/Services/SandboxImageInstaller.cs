using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.App.Services;

/// <summary>
/// The App's one-call glue for v1 sandbox-image provisioning (field failure 2026-07-17, twice: a
/// fresh <c>GitLoomEnv</c> import AND the tier-2 VM upgrade both leave the VM's docker image store
/// empty, so the first agent spawn fails). Runs Core's <see cref="SandboxImageAutoProvision"/> —
/// probe the two jail images in-distro, <c>docker build</c> any missing one from the app-bundled
/// <c>payload/images</c> sources — then, mirroring <see cref="DaemonUpdateToastPublisher"/>, posts
/// a shell toast only for outcomes that changed something (Installed / InstallFailed via the pure
/// <see cref="SandboxImageToast"/> policy); every quieter outcome stays oobe.log-only. The Core
/// flow never throws, and the caller is the startup background task — the launch path is never
/// blocked.
/// </summary>
internal static class SandboxImageInstaller
{
    public static Task RunAsync(Action<string> log) =>
        SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(new WslRunner()),
            SandboxImageProvisioner.DefaultBundledImagesDirectory(),
            log,
            CancellationToken.None,
            onOutcome: Publish);

    private static void Publish(SandboxImageProvisionOutcome outcome)
    {
        if (SandboxImageToast.TryCompose(outcome) is not { } toast)
        {
            return; // all-present / no-sources / faulted — no toast, ever
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime desktop
                && desktop.MainWindow?.DataContext is MainWindowViewModel shell)
            {
                shell.ShowToast(toast.Message, isError: toast.IsWarning);
            }
        });
    }
}
