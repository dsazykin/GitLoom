using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class InteractiveRebaseViewModel : ViewModelBase
{
    private readonly IInteractiveRebaseService _rebaseService;
    private readonly string _repoPath;
    private readonly string _baseSha;

    [ObservableProperty]
    private ObservableCollection<RebaseTodoItemViewModel> _plan = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    public Action? RequestClose { get; set; }

    public InteractiveRebaseViewModel(IInteractiveRebaseService rebaseService, string repoPath, string baseSha)
    {
        _rebaseService = rebaseService;
        _repoPath = repoPath;
        _baseSha = baseSha;
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
    }

    [RelayCommand]
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
                NewMessage = string.IsNullOrWhiteSpace(vm.NewMessage) ? null : vm.NewMessage
            }).ToList();

            await Task.Run(() => _rebaseService.StartInteractiveRebase(_repoPath, _baseSha, planList, ct), ct);
            
            RequestClose?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
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
}

public partial class RebaseTodoItemViewModel : ViewModelBase
{
    public string Sha { get; }
    public string Message { get; }
    
    [ObservableProperty]
    private RebaseAction[] _availableActions = Array.Empty<RebaseAction>();

    [ObservableProperty]
    private RebaseAction _action;

    [ObservableProperty]
    private string? _newMessage;

    public RebaseTodoItemViewModel(RebaseTodoItem model)
    {
        Sha = model.Sha;
        Message = model.Message;
        Action = model.Action;
        NewMessage = model.NewMessage ?? model.Message;
    }

    public void UpdateAvailableActions(bool canSquash)
    {
        var allActions = (RebaseAction[])Enum.GetValues(typeof(RebaseAction));
        AvailableActions = canSquash 
            ? allActions
            : allActions.Where(a => a != RebaseAction.Squash && a != RebaseAction.Fixup).ToArray();

        if (!canSquash && (Action == RebaseAction.Squash || Action == RebaseAction.Fixup))
        {
            Action = RebaseAction.Pick;
        }
    }
}
