using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

public partial class CreateBranchDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _branchName = string.Empty;

    [ObservableProperty]
    private bool _checkoutImmediately = true;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsCancelled { get; private set; } = false;
    public bool IsConfirmed { get; private set; } = false;

    // This action is wired up from the View to close the dialog
    public System.Action? CloseAction { get; set; }

    private bool CanConfirm => !string.IsNullOrWhiteSpace(BranchName);

    partial void OnBranchNameChanged(string value)
    {
        ErrorMessage = null;
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        IsConfirmed = true;
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsCancelled = true;
        CloseAction?.Invoke();
    }
}
