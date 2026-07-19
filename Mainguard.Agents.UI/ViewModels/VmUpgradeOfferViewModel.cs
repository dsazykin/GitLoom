using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Bootstrap;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The tier-2 in-place GitLoom OS upgrade offer + progress surface (P2-21 §3.6). Unlike the silent
/// tier-1 daemon refresh, replacing the VM takes minutes and is always CONSENTED: this VM starts in
/// the offer state (Upgrade / Later) and only runs the <see cref="IVmUpgradeOrchestrator"/> on an
/// explicit accept. Declining invokes <see cref="Declined"/> (the App remembers per session and
/// doesn't nag again) and closes. While running it renders the <see cref="VmUpgradePlan"/> step
/// list (the OOBE import-stages pattern, reusing <see cref="BootstrapStageViewModel"/> rows); a
/// failure surfaces the orchestrator's typed message — including the stranded-VHDX path when the
/// old distro was already retired — and never pretends anything succeeded.
///
/// <para>The dialog is not the only witness: when the App wires <see cref="LogSink"/> (its
/// oobe.log writer), every orchestrator progress line and the final typed result — failure kind,
/// promote strategy, stranded path — are logged too, so a failed upgrade is diagnosable from
/// oobe.log alone (field incident: the stranded outcome once lived only in this dialog).</para>
/// </summary>
public partial class VmUpgradeOfferViewModel : ViewModelBase
{
    private readonly IVmUpgradeOrchestrator? _orchestrator;
    private readonly VmUpgradeOptions? _options;

    /// <summary>Live constructor.</summary>
    public VmUpgradeOfferViewModel(
        IVmUpgradeOrchestrator orchestrator, VmUpgradeOptions options,
        string installedVersion, string expectedVersion)
    {
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        InstalledVersion = installedVersion;
        ExpectedVersion = expectedVersion;
        SeedSteps();
    }

    /// <summary>Design/render constructor: fixed versions, no orchestrator behind it.</summary>
    public VmUpgradeOfferViewModel(string installedVersion, string expectedVersion)
    {
        InstalledVersion = installedVersion;
        ExpectedVersion = expectedVersion;
        SeedSteps();
    }

    private void SeedSteps()
    {
        foreach (var step in VmUpgradePlan.Steps())
            Steps.Add(new BootstrapStageViewModel(step.Description));
    }

    /// <summary>The installed VM's payload version (what the user is on).</summary>
    public string InstalledVersion { get; }

    /// <summary>The app-bundled payload version (what the upgrade installs).</summary>
    public string ExpectedVersion { get; }

    /// <summary>The plan's step checklist (same row type as the OOBE import stages).</summary>
    public ObservableCollection<BootstrapStageViewModel> Steps { get; } = new();

    /// <summary>True in the initial consent state (Upgrade / Later showing).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaterCommand))]
    private bool _isOffering = true;

    /// <summary>True while the orchestrator runs. The window refuses to close in this state —
    /// the upgrade is not cancellable mid-flight (it replaces the distro).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpgradeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Set only for the stranded-after-retire failure: the host path of the VHDX holding
    /// the migrated data (shown selectable so the user can copy it for recovery/support).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStranded))]
    private string? _strandedVhdxPath;

    public bool IsStranded => !string.IsNullOrEmpty(StrandedVhdxPath);

    /// <summary>Wired from the View so Close works from the ViewModel.</summary>
    public Action? CloseAction { get; set; }

    /// <summary>Invoked when the user picks "Later" — the App sets its session-scoped
    /// don't-nag-again flag here.</summary>
    public Action? Declined { get; set; }

    /// <summary>The App's oobe.log writer (additive — the dialog behavior is unchanged): receives
    /// every orchestrator progress line plus one final-result line naming the outcome, the promote
    /// strategy, and the stranded VHDX path when there is one. Null (tests/design) = no logging.</summary>
    public Action<string>? LogSink { get; set; }

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private async Task UpgradeAsync()
    {
        if (_orchestrator is null || _options is null)
            return; // design/render instance

        IsOffering = false;
        IsRunning = true;
        ErrorMessage = null;
        StrandedVhdxPath = null;
        LogSink?.Invoke($"vm upgrade: accepted — upgrading payload {InstalledVersion} → {ExpectedVersion}");

        var progress = new Progress<string>(line =>
        {
            LogSink?.Invoke($"vm upgrade: {line}");
            ApplyProgressLine(line);
        });
        try
        {
            // Off the UI thread: the orchestrator does long process/IO work.
            var result = await Task.Run(
                () => _orchestrator.UpgradeAsync(_options, progress, CancellationToken.None)).ConfigureAwait(true);

            LogSink?.Invoke(DescribeResult(result));
            if (result.Succeeded)
            {
                foreach (var step in Steps)
                    step.State = BootstrapStageState.Done;
                IsComplete = true;
            }
            else
            {
                var running = Steps.FirstOrDefault(s => s.State == BootstrapStageState.Running);
                if (running is not null)
                    running.State = BootstrapStageState.Failed;
                ErrorMessage = result.Message;
                if (result.FailureKind == VmUpgradeFailureKind.StrandedAfterRetire)
                    StrandedVhdxPath = result.StagingVhdxPath;
            }
        }
        catch (Exception ex)
        {
            // The orchestrator returns typed results; anything else is a defect — still surfaced
            // honestly, never swallowed into a fake success.
            LogSink?.Invoke($"vm upgrade result: unexpected failure — {ex.Message}");
            ErrorMessage = $"The upgrade failed unexpectedly: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>The one final-result oobe.log line: outcome + failure kind, the promote strategy
    /// when the VHDX reached the canonical dir, and the data-bearing VHDX path when stranded.</summary>
    private static string DescribeResult(VmUpgradeResult result) =>
        (result.Succeeded ? "vm upgrade result: succeeded" : $"vm upgrade result: failed ({result.FailureKind})")
        + (result.PromoteStrategy is { } strategy ? $" [promote: {strategy}]" : "")
        + (result.StagingVhdxPath is { } vhdx ? $" [data vhdx: {vhdx}]" : "")
        + $" — {result.Message}";

    private bool CanUpgrade() => IsOffering && !IsRunning && _orchestrator is not null;

    [RelayCommand(CanExecute = nameof(IsOffering))]
    private void Later()
    {
        LogSink?.Invoke("vm upgrade: declined (Later) — not offering again this session");
        Declined?.Invoke();
        CloseAction?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => CloseAction?.Invoke();

    private bool CanClose() => !IsRunning;

    /// <summary>Marshalled to the UI thread by <see cref="Progress{T}"/>: a line matching a plan
    /// step's description advances the checklist (prior steps Done, that step Running); any other
    /// line becomes the running step's log tail.</summary>
    private void ApplyProgressLine(string line)
    {
        var index = -1;
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Name == line)
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            for (var i = 0; i < index; i++)
            {
                Steps[i].State = BootstrapStageState.Done;
                Steps[i].LogTail = null;
            }

            Steps[index].State = BootstrapStageState.Running;
            return;
        }

        var running = Steps.FirstOrDefault(s => s.State == BootstrapStageState.Running);
        if (running is not null)
            running.LogTail = line;
    }
}
