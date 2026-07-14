using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Agents.Bootstrap;
using GitLoom.Core.Exceptions;

namespace GitLoom.App.ViewModels;

/// <summary>The step the OOBE wizard is on. Drives which panel the single wizard window shows; the
/// transitions are produced by the SAME <see cref="OobeStateMachine"/> the P2-21 console driver ran.</summary>
public enum OobePhase
{
    /// <summary>Preflight diagnostics are running (read-only; nothing is modified yet).</summary>
    Diagnostics,
    /// <summary>All checks passed — the "Construct Sandbox" consent + single UAC gate.</summary>
    Consent,
    /// <summary>Features enabled; a Windows restart is required to finish before the VM import.</summary>
    Reboot,
    /// <summary>The GitLoomOS VM is importing (the P2-05 bootstrapper checklist).</summary>
    Importing,
    /// <summary>Provisioning is complete — ready to open the control center.</summary>
    Done,
    /// <summary>A diagnostic hard-stop: the machine cannot be provisioned until the user fixes it.</summary>
    Blocked,
    /// <summary>A step failed (feature enablement, missing payload, import) — an actionable error card.</summary>
    Error,
}

/// <summary>One preflight diagnostic row, projected for the wizard's diagnostics cards. Exposes boolean
/// state so the view encodes status by icon AND colour (never colour alone), consistent with the
/// design-system state-encoding rule.</summary>
public sealed class OobeDiagnosticViewModel
{
    public OobeDiagnosticViewModel(DiagnosticCheck check)
    {
        Title = check.Title;
        Message = check.Message;
        DocLink = check.DocLink;
        Status = check.Status;
    }

    public string Title { get; }
    public string? Message { get; }
    public string? DocLink { get; }
    public DiagnosticStatus Status { get; }

    public bool IsPass => Status == DiagnosticStatus.Pass;
    public bool IsFail => Status == DiagnosticStatus.Fail;
    public bool IsUnsupported => Status == DiagnosticStatus.Unsupported;
    public bool IsBlocking => Status != DiagnosticStatus.Pass;
    public bool HasDetail => !string.IsNullOrEmpty(Message);
    public bool HasDocLink => !string.IsNullOrEmpty(DocLink);
}

/// <summary>
/// P2-48 — the in-app Avalonia OOBE wizard. It renders P2-21's tested provisioning machinery inside
/// the app (no console): diagnostics cards, the "Construct Sandbox" consent + single UAC, reboot-resume,
/// and the VM-import checklist. It drives the <b>same</b> <see cref="OobeStateMachine"/> the console
/// driver exercised — the interactive consent step simply awaits a UI click before the machine's
/// <c>EnableFeatures</c> stage relaunches the elevated helper. This is UI over tested logic: it adds
/// no new provisioning/OS behaviour.
/// </summary>
public partial class OobeWizardViewModel : ViewModelBase
{
    private readonly OobeStateMachine? _machine;
    private readonly SystemDiagnostics? _diagnostics;
    private readonly IElevationLauncher? _elevationLauncher;
    private readonly GitLoomOsBootstrapper? _bootstrapper;
    private readonly OobeStageHandlers _handlers;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _consentTcs;
    private bool _userAborted;

    /// <summary>Raised (on the UI thread) when provisioning completes and the user opts to open GitLoom.
    /// The window's code-behind handles the swap to the control center.</summary>
    public event EventHandler? ProvisioningCompleted;

    /// <summary>Live constructor: wires the wizard's interactive handlers to the real machine +
    /// diagnostics + elevation launcher + P2-05 bootstrapper.</summary>
    public OobeWizardViewModel(
        OobeStateMachine machine,
        SystemDiagnostics diagnostics,
        IElevationLauncher elevationLauncher,
        GitLoomOsBootstrapper bootstrapper)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _elevationLauncher = elevationLauncher ?? throw new ArgumentNullException(nameof(elevationLauncher));
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));

        foreach (var name in bootstrapper.StepNames)
            ImportStages.Add(new BootstrapStageViewModel(name));

        _handlers = new OobeStageHandlers(RunDiagnosticsAsync, EnableFeaturesAsync, ImportVmAsync);
    }

    /// <summary>Design/render constructor: shows a fixed phase with representative data, no live machine.
    /// Used by the headless render harness and the visual designer.</summary>
    public OobeWizardViewModel(
        OobePhase phase,
        IEnumerable<OobeDiagnosticViewModel>? diagnostics = null,
        IEnumerable<BootstrapStageViewModel>? importStages = null,
        string? errorMessage = null)
    {
        _handlers = new OobeStageHandlers(RunDiagnosticsAsync, EnableFeaturesAsync, ImportVmAsync);
        _phase = phase;
        if (diagnostics is not null)
            foreach (var d in diagnostics)
                Diagnostics.Add(d);
        if (importStages is not null)
            foreach (var s in importStages)
                ImportStages.Add(s);
        _errorMessage = errorMessage;
    }

    /// <summary>The read-only preflight results (populated during <see cref="OobePhase.Diagnostics"/>,
    /// shown again on the consent and blocked panels).</summary>
    public ObservableCollection<OobeDiagnosticViewModel> Diagnostics { get; } = new();

    /// <summary>The P2-05 bootstrapper checklist rows, mirrored during <see cref="OobePhase.Importing"/>.</summary>
    public ObservableCollection<BootstrapStageViewModel> ImportStages { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiagnostics))]
    [NotifyPropertyChangedFor(nameof(IsConsent))]
    [NotifyPropertyChangedFor(nameof(IsReboot))]
    [NotifyPropertyChangedFor(nameof(IsImporting))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsBlocked))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    private OobePhase _phase = OobePhase.Diagnostics;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConstructSandboxCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool IsDiagnostics => Phase == OobePhase.Diagnostics;
    public bool IsConsent => Phase == OobePhase.Consent;
    public bool IsReboot => Phase == OobePhase.Reboot;
    public bool IsImporting => Phase == OobePhase.Importing;
    public bool IsDone => Phase == OobePhase.Done;
    public bool IsBlocked => Phase == OobePhase.Blocked;
    public bool IsError => Phase == OobePhase.Error;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string Title => "Welcome to GitLoom";

    /// <summary>The exact <c>Enable-WindowsOptionalFeature</c> PowerShell shown before the UAC prompt
    /// (transparency — the user sees precisely what runs elevated). Identical to the console driver.</summary>
    public string ConsentCommandText => InstallerCommands.EnableFeaturesPowerShell();

    /// <summary>The failing diagnostics only (what the blocked panel lists).</summary>
    public IEnumerable<OobeDiagnosticViewModel> Failures => Diagnostics.Where(d => d.IsBlocking);

    /// <summary>Starts (or resumes) driving the OOBE state machine. Safe to call again after a blocked
    /// or errored pass — the machine's persisted state makes each stage idempotent.</summary>
    [RelayCommand]
    public async Task StartAsync()
    {
        if (_machine is null)
            return; // design/render instance

        ErrorMessage = null;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var result = await _machine.RunAsync(_handlers, _cts.Token).ConfigureAwait(true);
            switch (result.Outcome)
            {
                case OobeRunOutcome.Completed:
                    DeleteResumeTaskBestEffort();
                    Phase = OobePhase.Done;
                    break;
                case OobeRunOutcome.AwaitingReboot:
                    Phase = OobePhase.Reboot;
                    break;
                case OobeRunOutcome.BlockedByDiagnostics:
                    Phase = OobePhase.Blocked;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (_userAborted)
            {
                // The user pressed Cancel during a long step (import / Docker wait). Surface the
                // actionable card — nothing is rolled back; "Try again" resumes, "Start over" restarts.
                _userAborted = false;
                Phase = OobePhase.Error;
                ErrorMessage = "Setup was cancelled. Nothing was rolled back — choose “Try again” "
                    + "to resume where you left off, or “Start over” to begin from the first step.";
            }
            else
            {
                // Cancelled at the consent gate; drop back to the diagnostics/consent view.
                Phase = Diagnostics.Any() && Diagnostics.All(d => d.IsPass) ? OobePhase.Consent : OobePhase.Diagnostics;
            }
        }
        catch (BootstrapException ex)
        {
            Phase = OobePhase.Error;
            ErrorMessage = FriendlyBootstrapError(ex);
        }
        catch (Exception ex)
        {
            Phase = OobePhase.Error;
            ErrorMessage =
                $"GitLoom setup could not finish: {ex.Message} " +
                "Your machine was left as-is. You can try again, and any progress already made is preserved.";
        }
    }

    // --- OobeStateMachine handlers (the SAME machine the console driver ran) ---

    private async Task<bool> RunDiagnosticsAsync(CancellationToken ct)
    {
        Phase = OobePhase.Diagnostics;
        IsBusy = true;
        try
        {
            var report = await _diagnostics!.RunAsync(ct).ConfigureAwait(true);
            Diagnostics.Clear();
            foreach (var check in report.Checks)
                Diagnostics.Add(new OobeDiagnosticViewModel(check));
            OnPropertyChanged(nameof(Failures));
            return report.CanProceed;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<FeatureEnableResult> EnableFeaturesAsync(CancellationToken ct)
    {
        // Show the consent gate and wait for the user to press "Construct Sandbox" (or cancel). This is
        // where the wizard is interactive; the machine's transition logic is unchanged.
        Phase = OobePhase.Consent;
        _consentTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (ct.Register(() => _consentTcs.TrySetCanceled(ct)))
        {
            var proceed = await _consentTcs.Task.ConfigureAwait(true);
            if (!proceed)
                throw new OperationCanceledException();
        }

        IsBusy = true;
        try
        {
            var result = await _elevationLauncher!.ConstructSandboxAsync(ct).ConfigureAwait(true);
            if (!result.FeaturesEnabled)
                throw new BootstrapException("EnableFeatures",
                    result.Error is { Length: > 0 } e
                        ? $"GitLoom could not enable the required Windows features: {e}"
                        : "GitLoom could not enable the required Windows features. "
                          + "Approve the Windows permission prompt and try again.");
            // The resume Scheduled Task only matters when a reboot will interrupt setup; when the features
            // were already enabled (RebootRequired=false) the same process continues straight to VM import,
            // so its registration is not part of success.
            var succeeded = result.FeaturesEnabled && (!result.RebootRequired || result.ResumeTaskRegistered);
            return new FeatureEnableResult(succeeded, result.RebootRequired);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportVmAsync(CancellationToken ct)
    {
        Phase = OobePhase.Importing;
        var progress = new Progress<BootstrapProgress>(ApplyImportProgress);
        // Runs off the UI thread; a BootstrapException propagates to StartAsync → the error card.
        await Task.Run(() => _bootstrapper!.RunAsync(progress, ct), ct).ConfigureAwait(true);
    }

    private void ApplyImportProgress(BootstrapProgress update)
    {
        var stage = ImportStages.FirstOrDefault(s => s.Name == update.StepName);
        if (stage is null)
            return;
        stage.State = update.State;
        if (update.Log is not null)
            stage.LogTail = update.Log;
    }

    // --- Interactive commands ---

    /// <summary>The consent action: proceeds past the "Construct Sandbox" gate into the single UAC prompt.</summary>
    [RelayCommand(CanExecute = nameof(CanConstruct))]
    private void ConstructSandbox() => _consentTcs?.TrySetResult(true);

    private bool CanConstruct() => !IsBusy;

    /// <summary>Cancels at the consent gate — nothing on the machine has been modified.</summary>
    [RelayCommand]
    private void CancelConsent() => _consentTcs?.TrySetResult(false);

    /// <summary>Aborts the in-flight step (running diagnostics, or the long VM-import / Docker-ready
    /// wait) so the user is never stranded on a spinner. The cancellation unwinds to the actionable
    /// card; nothing already provisioned is rolled back.</summary>
    [RelayCommand]
    private void CancelSetup()
    {
        _userAborted = true;
        _cts?.Cancel();
    }

    /// <summary>Discards persisted progress and restarts the wizard from the first step. Any partially
    /// provisioned WSL distro is left in place and re-used (the import step is idempotent); use the
    /// standalone uninstaller to fully remove it.</summary>
    [RelayCommand]
    private async Task StartOver()
    {
        _machine?.Reset();
        _userAborted = false;
        ErrorMessage = null;
        Diagnostics.Clear();
        OnPropertyChanged(nameof(Failures));
        foreach (var stage in ImportStages)
            stage.State = BootstrapStageState.Pending;
        Phase = OobePhase.Diagnostics;
        await StartAsync();
    }

    /// <summary>The in-app "Restart now" action (reboot phase): triggers the Windows restart. The
    /// elevated resume Scheduled Task relaunches GitLoom back into this wizard afterwards.</summary>
    [RelayCommand]
    private void RestartNow()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.ArgumentList.Add("/r");
            psi.ArgumentList.Add("/t");
            psi.ArgumentList.Add("0");
            Process.Start(psi);
        }
        catch
        {
            // shutdown.exe is Windows-only; on any other platform this is a no-op (the manual matrix
            // exercises the real reboot). The user can always restart manually.
        }
    }

    /// <summary>"Open GitLoom" on the done panel — hands off to the control center.</summary>
    [RelayCommand]
    private void OpenControlCenter() => ProvisioningCompleted?.Invoke(this, EventArgs.Empty);

    /// <summary>Re-run after a diagnostic block or a step error (idempotent — the machine resumes).</summary>
    [RelayCommand]
    private Task Retry() => StartAsync();

    private static void DeleteResumeTaskBestEffort()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            foreach (var a in InstallerCommands.UnregisterResumeTask())
                psi.ArgumentList.Add(a);
            Process.Start(psi)?.WaitForExit();
        }
        catch
        {
            // Windows-only; no-op elsewhere.
        }
    }

    private static string FriendlyBootstrapError(BootstrapException ex) =>
        ex.Message
        + " If the GitLoomOS payload is missing, reinstall GitLoom (a packaged build bundles it); "
        + "otherwise check the details above and try again — your enabled features and setup progress "
        + "are preserved.";
}
