using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Agents.Bootstrap;
using Mainguard.Git.Exceptions;

namespace GitLoom.App.ViewModels;

/// <summary>
/// One row of the P2-05 bootstrap checklist: a stage name, its <see cref="BootstrapStageState"/>, and
/// a one-line log tail. The boolean projections drive the state-encoded icon/colour in the view (a
/// non-colour-only encoding — pending/running/done/failed each show a distinct glyph).
/// </summary>
public partial class BootstrapStageViewModel : ViewModelBase
{
    public BootstrapStageViewModel(string name, BootstrapStageState state = BootstrapStageState.Pending, string? logTail = null)
    {
        _name = name;
        _state = state;
        _logTail = logTail;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private BootstrapStageState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLog))]
    private string? _logTail;

    public bool IsPending => State == BootstrapStageState.Pending;
    public bool IsRunning => State == BootstrapStageState.Running;
    public bool IsDone => State == BootstrapStageState.Done;
    public bool IsFailed => State == BootstrapStageState.Failed;
    public bool HasLog => !string.IsNullOrEmpty(LogTail);
}

/// <summary>
/// Drives the staged bootstrap checklist. Runs the <see cref="GitLoomOsBootstrapper"/> off the UI
/// thread and marshals each stage transition back through <see cref="Progress{T}"/>; the run is
/// cancellable between steps. Uses design tokens + component classes only (no raw colour in the view).
/// </summary>
public partial class BootstrapProgressViewModel : ViewModelBase
{
    private readonly GitLoomOsBootstrapper? _bootstrapper;
    private CancellationTokenSource? _cts;

    /// <summary>Design/render constructor: renders a fixed set of stages without a real bootstrapper.</summary>
    public BootstrapProgressViewModel(IEnumerable<BootstrapStageViewModel> stages)
    {
        foreach (var stage in stages)
            Stages.Add(stage);
    }

    /// <summary>Live constructor: seeds one pending stage per bootstrapper step, in order.</summary>
    public BootstrapProgressViewModel(GitLoomOsBootstrapper bootstrapper)
    {
        _bootstrapper = bootstrapper;
        foreach (var name in bootstrapper.StepNames)
            Stages.Add(new BootstrapStageViewModel(name));
    }

    public ObservableCollection<BootstrapStageViewModel> Stages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isComplete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Title line, so the view reads well without the caller wiring one.</summary>
    public string Title => "Setting up the Mainguard environment";

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (_bootstrapper is null)
            return;

        ErrorMessage = null;
        IsComplete = false;
        IsRunning = true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<BootstrapProgress>(Apply);
        try
        {
            // Off the UI thread: the steps do process/IO work that must never block the dispatcher.
            await Task.Run(() => _bootstrapper.RunAsync(progress, _cts.Token), _cts.Token).ConfigureAwait(true);
            IsComplete = true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Setup was cancelled.";
        }
        catch (BootstrapException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanRun() => !IsRunning && _bootstrapper is not null;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    // Marshalled onto the UI thread by Progress<T>; updates the matching stage row.
    private void Apply(BootstrapProgress update)
    {
        var stage = Stages.FirstOrDefault(s => s.Name == update.StepName);
        if (stage is null)
            return;

        stage.State = update.State;
        if (update.Log is not null)
            stage.LogTail = update.Log;
    }
}
