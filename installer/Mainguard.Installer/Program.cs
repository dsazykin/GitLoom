using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git;
using Mainguard.Git.Exceptions;
namespace Mainguard.Installer;

/// <summary>
/// The P2-21 unelevated OOBE driver. Flow:
/// <list type="number">
///   <item><b>Diagnostics</b> — preflight checks; on any hard fail it prints the actionable fixes and
///   exits WITHOUT modifying anything (the hard-stop invariant).</item>
///   <item><b>Construct Sandbox</b> — surfaces the raw <c>Enable-WindowsOptionalFeature</c> PowerShell,
///   then raises the single UAC prompt via the elevated helper (features + resume task).</item>
///   <item><b>Reboot / resume</b> — the elevated Scheduled Task re-invokes this exe with <c>--resume</c>.</item>
///   <item><b>Import VM</b> — delegates to the P2-05 <see cref="GitLoomOsBootstrapper"/>.</item>
/// </list>
/// The state machine persists to <c>oobe-state.json</c> after each transition, so every step is
/// resume-safe and idempotent.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var resume = args.Contains("--resume");
        var ct = CancellationToken.None;

        var wsl = new WslRunner();
        var store = new JsonOobeStateStore(JsonOobeStateStore.DefaultPath());
        var machine = new OobeStateMachine(store);

        var probe = new WindowsSystemProbe();
        var diagnostics = new SystemDiagnostics(probe, new WslStatusProbe(wsl));

        // Resolve the elevated helper from the RUNNING APP's own directory (where the build/publish
        // co-locates it), never the current working directory — so it is found regardless of where the
        // OOBE is launched from (`dotnet run`, a shortcut, a Scheduled-Task resume, or a packaged build).
        var appDir = AppContext.BaseDirectory;
        var oobeExe = Environment.ProcessPath ?? Path.Combine(appDir, "Mainguard.Installer.exe");
        var helperExe = Path.Combine(appDir, "Mainguard.Installer.Elevated.exe");
        // GitLoomPaths, not GetFolderPath: never let a "" fold this into a relative path.
        var resultPath = Path.Combine(Mainguard.Git.GitLoomPaths.DataRoot(), "elevated-result.json");
        var launcher = new RunAsElevationLauncher(helperExe, oobeExe, resultPath);

        var handlers = new OobeStageHandlers(
            RunDiagnostics: async c =>
            {
                Console.WriteLine("Running system diagnostics…");
                var report = await diagnostics.RunAsync(c).ConfigureAwait(false);
                foreach (var check in report.Checks)
                {
                    Console.WriteLine($"  [{(check.Status == DiagnosticStatus.Pass ? "PASS" : check.Status.ToString().ToUpperInvariant())}] {check.Title}");
                    if (check.IsBlocking)
                    {
                        Console.WriteLine($"        {check.Message}");
                        Console.WriteLine($"        See: {check.DocLink}");
                    }
                }
                if (report.HardStop)
                    Console.WriteLine("Setup cannot continue. Nothing on your machine was changed.");
                return report.CanProceed;
            },
            EnableFeatures: async c =>
            {
                Console.WriteLine();
                Console.WriteLine("Construct Sandbox: GitLoom will now enable two Windows features. It runs exactly this,");
                Console.WriteLine("with Administrator permission (one prompt):");
                Console.WriteLine();
                Console.WriteLine(InstallerCommands.EnableFeaturesPowerShell());
                Console.WriteLine();
                var result = await launcher.ConstructSandboxAsync(c).ConfigureAwait(false);
                if (!result.FeaturesEnabled)
                    Console.Error.WriteLine($"Feature enablement failed: {result.Error}");
                // The resume task only matters when a reboot will interrupt setup: on an
                // already-enabled machine the helper legitimately registers none, and requiring it
                // anyway failed every successful no-reboot run (audit fix #5).
                var succeeded = result.FeaturesEnabled && (!result.RebootRequired || result.ResumeTaskRegistered);
                if (result.FeaturesEnabled && !succeeded)
                    Console.Error.WriteLine($"Resume-task registration failed: {result.Error}");
                return new FeatureEnableResult(succeeded, result.RebootRequired);
            },
            ImportVm: async c =>
            {
                Console.WriteLine("Importing the GitLoomOS VM…");
                var options = new BootstrapOptions(
                    InstallDir: Path.Combine(Mainguard.Git.GitLoomPaths.DataRoot(), "vm"),
                    TarballPath: Path.Combine(AppContext.BaseDirectory, "payload", "GitLoomOS.tar.gz"));
                var ctx = new BootstrapContext(wsl, new BootstrapFileSystem(), new WslDaemonHealthProbe(wsl), options);
                var progress = new Progress<BootstrapProgress>(p =>
                {
                    if (p.Log is not null)
                        Console.WriteLine($"    {p.Log}");
                });
                await GitLoomOsBootstrapper.Create(ctx).RunAsync(progress, c).ConfigureAwait(false);
            },
            // Fix #4: a relaunch before the restart must re-print the restart instruction, not
            // sail into a VM import on half-enabled Windows features.
            RebootHasCompleted: (since, _) => Task.FromResult(SystemRebootEvidence.RebootedSince(since)));

        if (resume)
            Console.WriteLine("Resuming GitLoom setup after reboot…");

        try
        {
            var run = await machine.RunAsync(handlers, ct).ConfigureAwait(false);

            switch (run.Outcome)
            {
                case OobeRunOutcome.Completed:
                    // Reaching Done retires the resume Scheduled Task (self-deleting) and clears state.
                    DeleteResumeTask();
                    store.Clear();
                    Console.WriteLine("GitLoom is ready.");
                    return 0;
                case OobeRunOutcome.AwaitingReboot:
                    Console.WriteLine("Windows needs to restart to finish enabling the features.");
                    Console.WriteLine("After you restart, GitLoom setup will continue automatically.");
                    return 0;
                case OobeRunOutcome.BlockedByDiagnostics:
                    return 2;
                default:
                    return 1;
            }
        }
        catch (BootstrapException ex)
        {
            // A stage failed (e.g. the GitLoomOS payload is missing on a run-from-source build). Fail the
            // same way diagnostics hard-stop: a clean, actionable message — never an unhandled stack dump.
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Setup could not finish: {ex.Message}");
            Console.Error.WriteLine(
                "If the GitLoomOS payload is missing, place GitLoomOS.tar.gz in the installer's " +
                "'payload' folder next to the executable (a packaged build bundles it automatically), " +
                "then run setup again — your enabled features and resume state are preserved.");
            return 1;
        }
        catch (InvalidOperationException ex)
        {
            // The machine's own typed refusal (e.g. feature enablement reported no success) — a clean
            // message, never an unhandled stack dump (audit fix #5).
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Setup could not finish: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Best-effort deletion of the self-deleting resume Scheduled Task once setup completes.</summary>
    private static void DeleteResumeTask()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };
            foreach (var a in InstallerCommands.UnregisterResumeTask())
                psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi)?.WaitForExit();
        }
        catch
        {
            // schtasks is Windows-only; on any other platform this is a no-op.
        }
    }
}
