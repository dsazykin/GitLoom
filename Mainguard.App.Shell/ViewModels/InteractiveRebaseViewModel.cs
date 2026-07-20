using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

public partial class InteractiveRebaseViewModel : ViewModelBase
{
    private readonly IInteractiveRebaseService _rebaseService;
    private readonly IGitService? _gitService;
    private readonly string _repoPath;
    private readonly string _baseSha;
    private readonly Action<string, bool>? _showNotificationAction;

    [ObservableProperty]
    private ObservableCollection<RebaseTodoItemViewModel> _plan = new();

    // The row the keyboard shortcuts (P/R/S/F/E/D) act on.
    [ObservableProperty]
    private RebaseTodoItemViewModel? _selectedItem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartRebaseCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public Action? RequestClose { get; set; }

    public InteractiveRebaseViewModel(IInteractiveRebaseService rebaseService, string repoPath, string baseSha, Action<string, bool>? showNotificationAction = null, IGitService? gitService = null)
    {
        _rebaseService = rebaseService;
        _gitService = gitService;
        _repoPath = repoPath;
        _baseSha = baseSha;
        _showNotificationAction = showNotificationAction;
    }

    public void LoadPlan()
    {
        foreach (var item in Plan) item.PropertyChanged -= OnItemPropertyChanged;
        var planItems = _rebaseService.GetRebasePlan(_repoPath, _baseSha);
        Plan.Clear();
        foreach (var item in planItems)
        {
            var vm = new RebaseTodoItemViewModel(item);
            vm.PropertyChanged += OnItemPropertyChanged;
            Plan.Add(vm);
        }
        SelectedItem = Plan.FirstOrDefault();
        UpdateAvailableActions();
    }

    private bool _isUpdatingActions;

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RebaseTodoItemViewModel.Action))
        {
            UpdateAvailableActions();
        }
    }

    private void UpdateAvailableActions()
    {
        if (_isUpdatingActions) return;
        _isUpdatingActions = true;

        try
        {
            bool hasKeptCommit = false;
            foreach (var item in Plan)
            {
                item.UpdateAvailableActions(hasKeptCommit);

                if (item.Action != RebaseAction.Drop)
                {
                    hasKeptCommit = true;
                }
            }
        }
        finally
        {
            _isUpdatingActions = false;
        }

        // Keep the Start button's enabled state in lockstep with the service pre-flight
        // guards, so the UI never lets the user submit a plan the service will reject.
        StartRebaseCommand.NotifyCanExecuteChanged();
    }

    // Applies the keyboard-shortcut action (P/R/S/F/E/D) to the selected row, then
    // re-runs the fold/guard pass (which downgrades an illegal first-item squash/fixup).
    [RelayCommand]
    private void SetAction(RebaseAction action)
    {
        if (SelectedItem == null) return;
        SelectedItem.Action = action;
        UpdateAvailableActions();
    }

    // Mirrors InteractiveRebaseService's pre-flight validation so an invalid plan can
    // never be submitted: non-empty after drops, first kept item not squash/fixup, tree
    // clean, and no rebase already in progress. The repo-state checks are best-effort
    // (null service in tests → skipped; the service still guards authoritatively).
    private bool CanStartRebase()
    {
        if (IsBusy || Plan.Count == 0) return false;

        var kept = Plan.Where(p => p.Action != RebaseAction.Drop).ToList();
        if (kept.Count == 0) return false;
        if (kept[0].Action is RebaseAction.Squash or RebaseAction.Fixup) return false;

        if (_gitService != null)
        {
            try
            {
                if (_gitService.IsRebasing(_repoPath)) return false;
                if (_gitService.HasUncommittedChanges(_repoPath)) return false;
            }
            catch { /* be permissive; the service pre-flight is the source of truth */ }
        }

        return true;
    }

    [RelayCommand(CanExecute = nameof(CanStartRebase))]
    private async Task StartRebaseAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var planList = Plan.Select(vm => new RebaseTodoItem
            {
                Sha = vm.Sha,
                Action = vm.Action,
                Message = vm.Message,
                FullMessage = vm.FullMessage,
                // Only treat the message as "changed" when it differs from the full original,
                // so an untouched reword keeps git's full default body instead of the subject.
                NewMessage = (string.IsNullOrWhiteSpace(vm.NewMessage) || vm.NewMessage == vm.FullMessage)
                    ? null : vm.NewMessage
            }).ToList();

            await Task.Run(() => _rebaseService.StartInteractiveRebase(_repoPath, _baseSha, planList, ct), ct);

            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke(ex.Message, true);
            RequestClose?.Invoke();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Abort()
    {
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void MoveUp(RebaseTodoItemViewModel item)
    {
        var index = Plan.IndexOf(item);
        if (index <= 0) return;
        Plan.Move(index, index - 1);
        UpdateAvailableActions();
    }

    [RelayCommand]
    private void MoveDown(RebaseTodoItemViewModel item)
    {
        var index = Plan.IndexOf(item);
        if (index < 0 || index >= Plan.Count - 1) return;
        Plan.Move(index, index + 1);
        UpdateAvailableActions();
    }
}

public partial class RebaseTodoItemViewModel : ViewModelBase
{
    public string Sha { get; }
    public string Message { get; }
    public string FullMessage { get; }

    public string ShortSha => Sha.Length >= 7 ? Sha.Substring(0, 7) : Sha;

    public ObservableCollection<RebaseAction> AvailableActions { get; } = new((RebaseAction[])Enum.GetValues(typeof(RebaseAction)));

    [ObservableProperty]
    private RebaseAction _action;

    [ObservableProperty]
    private string? _newMessage;

    // True when this row folds into the preceding kept commit (squash/fixup) — drives the
    // grouping rail in the plan list so the fold is visible at a glance.
    [ObservableProperty]
    private bool _isFolded;

    public RebaseTodoItemViewModel(RebaseTodoItem model)
    {
        Sha = model.Sha;
        Message = model.Message;
        FullMessage = string.IsNullOrEmpty(model.FullMessage) ? model.Message : model.FullMessage;
        Action = model.Action;
        // Default the editable message to the full original body so reword doesn't truncate.
        NewMessage = model.NewMessage ?? FullMessage;
    }

    public void UpdateAvailableActions(bool canSquash)
    {
        // First, safe-guard the action if it's going to become invalid
        if (!canSquash && (Action == RebaseAction.Squash || Action == RebaseAction.Fixup))
        {
            Action = RebaseAction.Pick;
        }

        // A squash/fixup row folds into the preceding kept commit.
        IsFolded = Action is RebaseAction.Squash or RebaseAction.Fixup;

        // Then update the collection without replacing the instance, preventing Avalonia from temporarily setting SelectedItem to null
        if (canSquash)
        {
            if (!AvailableActions.Contains(RebaseAction.Squash))
            {
                // To keep logical order, maybe we just clear and add all? But clearing sets to null.
                // Re-inserting at specific indexes is safe.
                AvailableActions.Insert(2, RebaseAction.Squash);
                AvailableActions.Insert(3, RebaseAction.Fixup);
            }
        }
        else
        {
            AvailableActions.Remove(RebaseAction.Squash);
            AvailableActions.Remove(RebaseAction.Fixup);
        }
    }
}
