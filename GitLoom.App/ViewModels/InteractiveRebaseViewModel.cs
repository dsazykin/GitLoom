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
        var planItems = _rebaseService.GetRebasePlan(_repoPath, _baseSha);
        Plan.Clear();
        for (int i = 0; i < planItems.Count; i++)
        {
            Plan.Add(new RebaseTodoItemViewModel(planItems[i], i == 0));
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
    
    public RebaseAction[] AvailableActions { get; }

    [ObservableProperty]
    private RebaseAction _action;

    [ObservableProperty]
    private string? _newMessage;

    public RebaseTodoItemViewModel(RebaseTodoItem model, bool isFirst)
    {
        Sha = model.Sha;
        Message = model.Message;
        
        var allActions = (RebaseAction[])Enum.GetValues(typeof(RebaseAction));
        AvailableActions = isFirst 
            ? allActions.Where(a => a != RebaseAction.Squash && a != RebaseAction.Fixup).ToArray()
            : allActions;

        Action = (isFirst && (model.Action == RebaseAction.Squash || model.Action == RebaseAction.Fixup))
            ? RebaseAction.Pick
            : model.Action;
            
        NewMessage = model.NewMessage ?? model.Message;
    }
}
