using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Agents.Adapters;
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
    /// <summary>Provisioning succeeded — the user picks which agent CLIs to install (P2-22 §J-5).
    /// Placed here deliberately: the VM now exists (installs run inside it) and the daemon is healthy,
    /// so this is the first moment an install can actually work. Clearly skippable — a user with zero
    /// CLIs still gets a working GitLoom and adds them later from Settings → Agent CLIs.</summary>
    AgentClis,
    /// <summary>The repo-onboarding step (PR2): point GitLoom at your git repositories (one folder of
    /// repos, or individual picks) and copy the chosen ones into GitLoom OS. Placed after
    /// <see cref="AgentClis"/> deliberately — provisioning a repo needs the healthy daemon, which is
    /// guaranteed by then. Clearly skippable — a user with zero repos onboarded still finishes setup
    /// and every repo is copied automatically the first time it is opened.</summary>
    RepoOnboarding,
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
    private readonly AgentCliInstaller? _cliInstaller;
    private CancellationTokenSource? _cliCts;
    private readonly RepoOnboardingViewModel _repoStep;
    private readonly OobeStageHandlers _handlers;
    private readonly SynchronizationContext? _uiContext;
    private readonly Action? _resumeTaskSweep;
    private readonly Func<OobeInstanceLock?>? _instanceLockFactory;
    private CancellationTokenSource? _cts;
    private TaskCompletionSource<bool>? _consentTcs;
    private bool _userAborted;
    private bool _runInFlight;
    private bool _consentAutoProceed;

    /// <summary>Raised (on the UI thread) when provisioning completes and the user opts to open GitLoom.
    /// The window's code-behind handles the swap to the control center.</summary>
    public event EventHandler? ProvisioningCompleted;

    /// <summary>Live constructor: wires the wizard's interactive handlers to the real machine +
    /// diagnostics + elevation launcher + P2-05 bootstrapper.</summary>
    public OobeWizardViewModel(
        OobeStateMachine machine,
        SystemDiagnostics diagnostics,
        IElevationLauncher elevationLauncher,
        GitLoomOsBootstrapper bootstrapper,
        Action? resumeTaskSweep = null,
        Func<OobeInstanceLock?>? instanceLockFactory = null,
        AgentCliInstaller? cliInstaller = null,
        Func<CancellationToken, Task<bool>>? vmIsRegistered = null,
        Func<DateTimeOffset, CancellationToken, Task<bool>>? rebootHasCompleted = null,
        GitLoom.Core.Services.IRepoDiscoveryService? repoDiscovery = null,
        Func<Task<string?>>? pickRepoRootFolder = null,
        Func<Task<IReadOnlyList<string>>>? pickIndividualRepoFolders = null,
        Func<string, CancellationToken, Task>? provisionRepo = null,
        Action<string>? persistRepo = null,
        GitLoom.Core.Services.ISettingsService? settingsService = null)
    {
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
        _elevationLauncher = elevationLauncher ?? throw new ArgumentNullException(nameof(elevationLauncher));
        _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
        // Anti-zombie hygiene for the elevated resume Scheduled Task (ResumeTaskGuard.Sweep, wired by
        // App): invoked after every pass that does NOT hand off to a reboot, so an abandoned setup
        // never leaves an ONLOGON task behind. Null in tests/design instances.
        _resumeTaskSweep = resumeTaskSweep;
        // Cross-process single-instance guard: two processes (the interactive wizard + the resume
        // task's relaunch) must never drive the one state machine over the same files. Null (tests)
        // skips process-level locking; the shipped App always provides the real lock.
        _instanceLockFactory = instanceLockFactory;
        // Null (tests / no VM) simply skips the agent-CLI step: Completed goes straight to Done.
        _cliInstaller = cliInstaller;
        // Repo-onboarding seams (PR2): all injected so the step's logic is unit-testable with fakes.
        // The step's state + commands live in the shared RepoOnboardingViewModel engine (also behind
        // the post-setup Tools → "Add Repos to GitLoom OS…" window) and are forwarded 1:1 below.
        // With no scanner or no provisioner wired (tests / no daemon) the step is skipped entirely —
        // the wizard behaves exactly as before this step existed.
        _repoStep = new RepoOnboardingViewModel(
            repoDiscovery, pickRepoRootFolder, pickIndividualRepoFolders,
            provisionRepo, persistRepo, settingsService);
        _repoStep.PropertyChanged += OnRepoStepPropertyChanged;

        foreach (var name in bootstrapper.StepNames)
            ImportStages.Add(new BootstrapStageViewModel(name));

        // The construction-time (UI) context. OobeStateMachine awaits its handlers with
        // ConfigureAwait(false), so from the second stage onward it may invoke them on a THREAD-POOL
        // thread — every handler re-marshals onto this context first (SwitchToUiContext) so all
        // bindable state (Phase, IsBusy, the collections) only ever mutates on the UI thread.
        // Null (unit tests, no dispatcher) ⇒ the switch is a synchronous no-op.
        _uiContext = SynchronizationContext.Current;

        // vmIsRegistered lets the machine catch a stale "VM imported" flag on resume (the user ran
        // `wsl --unregister GitLoomEnv` between runs, e.g. to take a rebuilt payload) and rewind to
        // re-import instead of handing the wizard to steps that operate on a distro that is gone.
        // rebootHasCompleted keeps a relaunch-before-restart parked on the restart panel (fix #4).
        _handlers = new OobeStageHandlers(
            RunDiagnosticsAsync, EnableFeaturesAsync, ImportVmAsync, vmIsRegistered, rebootHasCompleted);
    }

    /// <summary>Design/render constructor: shows a fixed phase with representative data, no live machine.
    /// Used by the headless render harness and the visual designer.</summary>
    public OobeWizardViewModel(
        OobePhase phase,
        IEnumerable<OobeDiagnosticViewModel>? diagnostics = null,
        IEnumerable<BootstrapStageViewModel>? importStages = null,
        string? errorMessage = null,
        IEnumerable<AgentCliRowViewModel>? cliOptions = null,
        bool isInstallingClis = false,
        IEnumerable<OnboardRepoRowViewModel>? repoRows = null,
        bool isProvisioningRepos = false,
        bool isRepoScanning = false,
        string? repoNotice = null)
    {
        _handlers = new OobeStageHandlers(RunDiagnosticsAsync, EnableFeaturesAsync, ImportVmAsync);
        _phase = phase;
        if (diagnostics is not null)
            foreach (var d in diagnostics)
                Diagnostics.Add(d);
        if (importStages is not null)
            foreach (var s in importStages)
                ImportStages.Add(s);
        if (cliOptions is not null)
            foreach (var c in cliOptions)
                AttachCliRow(c);
        _isInstallingClis = isInstallingClis;
        _repoStep = new RepoOnboardingViewModel(repoRows, isProvisioningRepos, isRepoScanning, repoNotice);
        _repoStep.PropertyChanged += OnRepoStepPropertyChanged;
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
    [NotifyPropertyChangedFor(nameof(IsAgentClis))]
    [NotifyPropertyChangedFor(nameof(IsRepoOnboarding))]
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
    public bool IsAgentClis => Phase == OobePhase.AgentClis;
    public bool IsRepoOnboarding => Phase == OobePhase.RepoOnboarding;
    public bool IsDone => Phase == OobePhase.Done;
    public bool IsBlocked => Phase == OobePhase.Blocked;
    public bool IsError => Phase == OobePhase.Error;
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public string Title => "Welcome to Mainguard";

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

        // One machine pass at a time. StartCommand guards itself, but Retry/StartOver/the self-healing
        // consent commands all funnel here through DIFFERENT commands — without this guard a second
        // pass could re-enter EnableFeatures and orphan the first pass's live consent gate.
        if (_runInFlight)
            return;
        _runInFlight = true;

        // Cross-PROCESS exclusion (the in-process guard above cannot see the resume task's relaunch):
        // hold the machine-wide lock for the whole pass. A second GitLoom driving setup concurrently
        // is exactly the state-file race behind the zombie-resume incident — refuse it with a named,
        // actionable card instead of corrupting oobe-state.json.
        OobeInstanceLock? instanceLock = null;
        if (_instanceLockFactory is not null)
        {
            instanceLock = _instanceLockFactory();
            if (instanceLock is null)
            {
                _runInFlight = false;
                Phase = OobePhase.Error;
                ErrorMessage =
                    "Another Mainguard setup is already running on this machine — probably a second "
                    + "Mainguard window, or the automatic after-restart setup that starts when you log in. "
                    + "Close the other Mainguard window (check the taskbar) and press “Try again”.";
                return;
            }
        }

        ErrorMessage = null;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var awaitingReboot = false;
        try
        {
            var result = await _machine.RunAsync(_handlers, _cts.Token).ConfigureAwait(true);
            switch (result.Outcome)
            {
                case OobeRunOutcome.Completed:
                    // Provisioning succeeded — offer the agent-CLI picker while the freshly booted VM
                    // is right there to install into. With no installer wired (tests), straight to Done.
                    await EnterAgentCliStepAsync().ConfigureAwait(true);
                    break;
                case OobeRunOutcome.AwaitingReboot:
                    awaitingReboot = true;
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
                // Cancelled at the consent gate; drop back to the consent view. (Always Consent: the
                // gate is only reachable once diagnostics passed, and on a resumed run the Diagnostics
                // collection can legitimately be empty — keying the phase off it stranded the user on
                // an idle diagnostics panel.) The consent buttons re-arm a fresh machine pass — see
                // ConstructSandbox — so this re-shown panel is never dead.
                Phase = OobePhase.Consent;
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
                $"Mainguard setup could not finish: {ex.Message} " +
                "Your machine was left as-is. You can try again, and any progress already made is preserved.";
        }
        finally
        {
            // Resume-task hygiene on EVERY pass ending except the reboot hand-off (the one moment the
            // task legitimately exists): done, blocked, error, and cancel must never leave the elevated
            // ONLOGON task behind. Off the UI thread — schtasks is a child process.
            if (!awaitingReboot && _resumeTaskSweep is { } sweep)
                _ = Task.Run(sweep);
            instanceLock?.Dispose();
            _runInFlight = false;
        }
    }

    // --- OobeStateMachine handlers (the SAME machine the console driver ran) ---

    private async Task<bool> RunDiagnosticsAsync(CancellationToken ct)
    {
        await SwitchToUiContext();
        // Any pass that re-runs diagnostics must re-ask for consent — a pending auto-proceed from a
        // self-healed consent click must never leak past a fresh diagnostics run.
        _consentAutoProceed = false;
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
        await SwitchToUiContext();
        // Show the consent gate and wait for the user to press "Construct Sandbox" (or cancel). This is
        // where the wizard is interactive; the machine's transition logic is unchanged. When the click
        // arrived BEFORE this pass (a self-healed consent button re-started the machine), consent was
        // already given — sail straight through instead of re-arming a gate nobody will click.
        if (_consentAutoProceed)
        {
            _consentAutoProceed = false;
            Phase = OobePhase.Consent;
        }
        else
        {
            // Arm the gate BEFORE showing the panel — the consent buttons must never be visible while
            // the gate they resolve does not exist yet.
            _consentTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Phase = OobePhase.Consent;
            using (ct.Register(() => _consentTcs.TrySetCanceled(ct)))
            {
                var proceed = await _consentTcs.Task.ConfigureAwait(true);
                if (!proceed)
                    throw new OperationCanceledException();
            }
        }

        IsBusy = true;
        try
        {
            var result = await _elevationLauncher!.ConstructSandboxAsync(ct).ConfigureAwait(true);
            if (!result.FeaturesEnabled)
                throw new BootstrapException("EnableFeatures",
                    result.Error is { Length: > 0 } e
                        ? $"Mainguard could not enable the required Windows features: {e}"
                        : "Mainguard could not enable the required Windows features. "
                          + "Approve the Windows permission prompt and try again.");
            // The resume Scheduled Task only matters when a reboot will interrupt setup; when the features
            // were already enabled (RebootRequired=false) the same process continues straight to VM import,
            // so its registration is not part of success. When it IS needed and failed, surface the
            // helper's actual error instead of letting the machine throw a vague "reported no success".
            if (result.RebootRequired && !result.ResumeTaskRegistered)
                throw new BootstrapException("EnableFeatures",
                    result.Error is { Length: > 0 } taskError
                        ? $"The Windows features were enabled, but Mainguard could not register the task "
                          + $"that resumes setup after the restart: {taskError}"
                        : "The Windows features were enabled, but Mainguard could not register the task "
                          + "that resumes setup after the restart.");
            return new FeatureEnableResult(true, result.RebootRequired);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportVmAsync(CancellationToken ct)
    {
        await SwitchToUiContext();
        Phase = OobePhase.Importing;
        // Created AFTER the switch: Progress<T> captures the current context, so the per-step
        // callbacks marshal back to the UI thread too.
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

    /// <summary>The consent action: proceeds past the "Construct Sandbox" gate into the single UAC prompt.
    /// Self-healing: when the consent panel is showing but its gate's machine pass has already ended
    /// (e.g. the user cancelled at this gate earlier and the wizard dropped back to the consent view),
    /// the click starts a fresh pass and carries the consent through it — the button is never dead.</summary>
    [RelayCommand(CanExecute = nameof(CanConstruct))]
    private async Task ConstructSandbox()
    {
        if (_consentTcs is { } gate && !gate.Task.IsCompleted)
        {
            gate.TrySetResult(true);
            return;
        }
        if (_runInFlight)
            return; // a pass is running past the gate already (e.g. elevation in flight) — nothing to re-arm
        _consentAutoProceed = true;
        await StartAsync();
    }

    private bool CanConstruct() => !IsBusy;

    /// <summary>Cancels at the consent gate — nothing on the machine has been modified. With no live
    /// gate (the pass already ended) there is nothing running to cancel; the click is a safe no-op.</summary>
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
        _consentAutoProceed = false;
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

    // --- Agent-CLI picker (P2-22 §J-5 — the OOBE surface over AgentCliInstaller) ---

    /// <summary>The CLIs the pinned starter channel offers, with live install state per row.</summary>
    public ObservableCollection<AgentCliRowViewModel> CliOptions { get; } = new();

    /// <summary>True while the channel manifest + in-VM installed-state probes are being read —
    /// the list shows a named "checking" line during it, never a blank panel.</summary>
    [ObservableProperty]
    private bool _isCliLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCliLoadError))]
    private string? _cliLoadError;

    public bool HasCliLoadError => !string.IsNullOrEmpty(CliLoadError);

    /// <summary>True while a chosen set is installing (network + npm — minutes, not seconds).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallSelectedClisCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSkipClis))]
    [NotifyPropertyChangedFor(nameof(ShowContinueClis))]
    [NotifyPropertyChangedFor(nameof(ShowInstallCliAccent))]
    [NotifyPropertyChangedFor(nameof(ShowInstallCliPrimary))]
    private bool _isInstallingClis;

    /// <summary>At least one row is installed (probe-verified) — the step's primary becomes Continue.</summary>
    public bool AnyCliInstalled => CliOptions.Any(o => o.IsInstalled);

    private bool AnyCliInstallable => CliOptions.Any(o => !o.IsInstalled);

    // Footer button matrix, state-derived (no session memory): nothing installed → Skip + Install
    // (the view's one Accent); something installed → Continue is the Accent and Install demotes to
    // Primary for the remainder; installing → Cancel only.
    public bool ShowSkipClis => !IsInstallingClis && !AnyCliInstalled;
    public bool ShowContinueClis => !IsInstallingClis && AnyCliInstalled;
    public bool ShowInstallCliAccent => !IsInstallingClis && !AnyCliInstalled && AnyCliInstallable;
    public bool ShowInstallCliPrimary => !IsInstallingClis && AnyCliInstalled && AnyCliInstallable;

    private async Task EnterAgentCliStepAsync()
    {
        if (_cliInstaller is null)
        {
            EnterRepoOnboardingStep();
            return;
        }

        Phase = OobePhase.AgentClis;
        await LoadCliOptionsAsync().ConfigureAwait(true);
    }

    /// <summary>Loads (or reloads — the load-error "Try again") the channel offer + installed state.
    /// A failure here never blocks setup: the error names the cause and the skip path stays live.</summary>
    [RelayCommand]
    private async Task LoadCliOptionsAsync()
    {
        if (_cliInstaller is null)
            return;
        CliLoadError = null;
        IsCliLoading = true;
        try
        {
            var options = await _cliInstaller.ListAsync(CancellationToken.None).ConfigureAwait(true);
            foreach (var row in CliOptions)
                row.PropertyChanged -= OnCliRowChanged;
            CliOptions.Clear();
            foreach (var option in options)
                AttachCliRow(new AgentCliRowViewModel(option));
        }
        catch (Exception ex)
        {
            CliLoadError = $"Mainguard could not read its agent-CLI catalog: {ex.Message} "
                + "You can try again, or skip — agents can be added anytime from the Agent CLIs settings.";
        }
        finally
        {
            IsCliLoading = false;
            RaiseCliStateChanged();
        }
    }

    /// <summary>
    /// Installs the checked CLIs one at a time (the shared npm prefix must never see two concurrent
    /// installs), driving each row's own progress state. Failure-isolated: a CLI that fails shows its
    /// actionable cause on its row and the rest still install — and nothing here can ever fail the
    /// OOBE itself (Continue/Skip stay reachable in every terminal state).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInstallSelectedClis))]
    private async Task InstallSelectedClisAsync()
    {
        if (_cliInstaller is null)
            return;
        var chosen = CliOptions.Where(o => o.IsSelected && !o.IsInstalled).ToList();
        if (chosen.Count == 0)
            return;

        _cliCts?.Dispose();
        _cliCts = new CancellationTokenSource();
        var ct = _cliCts.Token;
        IsInstallingClis = true;
        try
        {
            foreach (var row in chosen)
            {
                if (ct.IsCancellationRequested)
                    break; // later rows keep their checkbox — re-Install or Skip both work
                row.IsFailed = false;
                row.IsInstalling = true;
                row.StatusMessage = "Downloading, verifying, and installing — this can take a few "
                    + "minutes on a slow connection.";
                try
                {
                    var outcomes = await _cliInstaller
                        .InstallAsync(new[] { row.Id }, progress: null, ct).ConfigureAwait(true);
                    var outcome = outcomes[0];
                    if (outcome.Succeeded)
                    {
                        row.IsInstalled = true;
                        row.IsSelected = false;
                        row.StatusMessage = null;
                    }
                    else
                    {
                        row.IsFailed = true;
                        row.StatusMessage = outcome.Error;
                    }
                }
                catch (OperationCanceledException)
                {
                    row.StatusMessage = "Cancelled. Nothing else was changed — install it anytime "
                        + "from the Agent CLIs settings.";
                    break;
                }
                finally
                {
                    row.IsInstalling = false;
                }
            }
        }
        finally
        {
            IsInstallingClis = false;
        }
    }

    private bool CanInstallSelectedClis() =>
        !IsInstallingClis && CliOptions.Any(o => o.IsSelected && o.CanSelect);

    /// <summary>Aborts the in-flight CLI installs (the user is never stranded watching npm). The
    /// in-flight row reports the cancellation on itself; the step stays open for retry or skip.</summary>
    [RelayCommand]
    private void CancelCliInstall() => _cliCts?.Cancel();

    /// <summary>Both "Skip for now" and "Continue": the picker is over, on to the next step.
    /// Deliberately unconditional — CLI trouble must never gate finishing setup.</summary>
    [RelayCommand]
    private void FinishCliStep() => EnterRepoOnboardingStep();

    private void AttachCliRow(AgentCliRowViewModel row)
    {
        row.PropertyChanged += OnCliRowChanged;
        CliOptions.Add(row);
    }

    private void OnCliRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AgentCliRowViewModel.IsSelected) or nameof(AgentCliRowViewModel.IsInstalled))
            RaiseCliStateChanged();
    }

    private void RaiseCliStateChanged()
    {
        OnPropertyChanged(nameof(AnyCliInstalled));
        OnPropertyChanged(nameof(ShowSkipClis));
        OnPropertyChanged(nameof(ShowContinueClis));
        OnPropertyChanged(nameof(ShowInstallCliAccent));
        OnPropertyChanged(nameof(ShowInstallCliPrimary));
        InstallSelectedClisCommand.NotifyCanExecuteChanged();
    }

    // --- Repo onboarding (PR2 — copy host repos into GitLoom OS so the app is usable on day one) ---
    //
    // The step's whole state machine lives in the shared RepoOnboardingViewModel engine (the same
    // engine behind the post-setup Tools → "Add Repos to GitLoom OS…" window, so the two surfaces
    // cannot drift). Everything below is a 1:1 forward — property and command names are unchanged,
    // so the wizard's XAML and tests are agnostic to the extraction; the engine's PropertyChanged
    // is re-raised through this VM verbatim (OnRepoStepPropertyChanged) because the names match.

    /// <summary>The discovered repositories, one row each with live copy state.</summary>
    public ObservableCollection<OnboardRepoRowViewModel> RepoRows => _repoStep.RepoRows;

    /// <summary>True while a chosen folder is being scanned for git repositories.</summary>
    public bool IsRepoScanning => _repoStep.IsRepoScanning;

    /// <summary>An advisory line on the step (empty scan, skipped non-repo picks, a scan error).
    /// Never blocks anything — the choice buttons and the skip path stay live under it.</summary>
    public string? RepoNotice => _repoStep.RepoNotice;

    public bool HasRepoNotice => _repoStep.HasRepoNotice;

    /// <summary>True while a chosen set is copying into GitLoom OS (a mirror clone per repo —
    /// minutes for a large repository, not seconds).</summary>
    public bool IsProvisioningRepos => _repoStep.IsProvisioningRepos;

    public bool HasRepoRows => _repoStep.HasRepoRows;

    /// <summary>The "how do you keep your repos" choice view — shown until a scan/pick produced rows.</summary>
    public bool IsRepoChoice => _repoStep.IsRepoChoice;

    /// <summary>At least one row completed the whole pipeline — the step's primary becomes Continue.</summary>
    public bool AnyRepoOnboarded => _repoStep.AnyRepoOnboarded;

    // Footer button matrix, state-derived exactly like the CLI step (no session memory): nothing
    // onboarded → Skip + Copy (the view's one Accent); something onboarded → Continue is the Accent
    // and Copy demotes to Primary for the remainder; copying → Cancel only.
    public bool ShowSkipRepos => _repoStep.ShowSkipRepos;
    public bool ShowContinueRepos => _repoStep.ShowContinueRepos;
    public bool ShowCopyReposAccent => _repoStep.ShowCopyReposAccent;
    public bool ShowCopyReposPrimary => _repoStep.ShowCopyReposPrimary;

    /// <summary>Back to the choice view (wrong folder picked) — only before anything was copied.</summary>
    public bool ShowRepoChooseAgain => _repoStep.ShowRepoChooseAgain;

    /// <summary>Choice A — one scanned folder (persisted as the sidebar's auto-detect path).</summary>
    public IAsyncRelayCommand PickRepoFolderCommand => _repoStep.PickRepoFolderCommand;

    /// <summary>Choice B — individual picks, each validated, deduped by path.</summary>
    public IAsyncRelayCommand PickIndividualReposCommand => _repoStep.PickIndividualReposCommand;

    /// <summary>The sequential, per-row failure-isolated copy run.</summary>
    public IAsyncRelayCommand CopySelectedReposCommand => _repoStep.CopySelectedReposCommand;

    /// <summary>Aborts the in-flight copies; the in-flight row reports the cancellation on itself.</summary>
    public IRelayCommand CancelRepoCopyCommand => _repoStep.CancelRepoCopyCommand;

    /// <summary>Back to the two-choice view — only offered before anything was copied.</summary>
    public IRelayCommand ChooseReposAgainCommand => _repoStep.ChooseReposAgainCommand;

    private void EnterRepoOnboardingStep()
    {
        // No scanner or no provisioner wired (tests / no daemon): the step cannot function — skip it.
        if (!_repoStep.CanOnboard)
        {
            Phase = OobePhase.Done;
            return;
        }

        Phase = OobePhase.RepoOnboarding;
    }

    /// <summary>Re-raises the engine's change notifications through this VM: the wizard view binds
    /// the forwarded properties above, whose names match the engine's 1:1.</summary>
    private void OnRepoStepPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        => OnPropertyChanged(e);

    /// <summary>Both "Skip for now" and "Continue": the step is over, on to the done panel.
    /// Deliberately unconditional — repo trouble must never gate finishing setup (a repo is also
    /// copied automatically the first time it is opened).</summary>
    [RelayCommand]
    private void FinishRepoStep() => Phase = OobePhase.Done;

    /// <summary>Re-run after a diagnostic block or a step error (idempotent — the machine resumes).</summary>
    [RelayCommand]
    private Task Retry() => StartAsync();

    /// <summary>
    /// Hops onto the wizard's construction-time (UI) context when the caller is not already on it.
    /// The state machine invokes handlers after <c>ConfigureAwait(false)</c> awaits — i.e. possibly
    /// from a thread-pool thread — and Avalonia bindable state must only mutate on the UI thread.
    /// A completed no-op when there is no context (unit tests) or we are already on it. This is an
    /// awaitable (not a Task-returning method) so the CONTINUATION itself is posted to the context —
    /// awaiting a context-posted Task from a pool thread would resume on the pool again.
    /// </summary>
    private UiContextAwaitable SwitchToUiContext() => new(_uiContext);

    private readonly struct UiContextAwaitable
    {
        private readonly SynchronizationContext? _context;
        public UiContextAwaitable(SynchronizationContext? context) => _context = context;
        public Awaiter GetAwaiter() => new(_context);

        public readonly struct Awaiter : System.Runtime.CompilerServices.ICriticalNotifyCompletion
        {
            private readonly SynchronizationContext? _context;
            public Awaiter(SynchronizationContext? context) => _context = context;
            public bool IsCompleted => _context is null || ReferenceEquals(SynchronizationContext.Current, _context);
            public void GetResult() { }
            public void OnCompleted(Action continuation) => _context!.Post(static s => ((Action)s!)(), continuation);
            public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
        }
    }

    private static string FriendlyBootstrapError(BootstrapException ex)
    {
        // The payload hint only makes sense for the VM-import step — the only one that reads the tarball.
        // By the time any later step (e.g. Docker readiness) runs, the payload is already imported, so
        // blaming it there is just misleading. Key off the message actually mentioning the tarball.
        var aboutPayload = ex.Message.Contains("tarball", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("payload", StringComparison.OrdinalIgnoreCase);
        var tail = aboutPayload
            ? " Reinstall Mainguard (a packaged build bundles the Mainguard OS payload) or stage the payload, then try again."
            : " Your enabled features and setup progress are preserved — try again, or start over to run setup from the top.";
        return ex.Message + tail;
    }
}
