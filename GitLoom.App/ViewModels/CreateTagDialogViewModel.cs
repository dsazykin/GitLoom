using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

public partial class CreateTagDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _tagName = string.Empty;

    [ObservableProperty]
    private bool _isAnnotated;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private string _targetSha = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public bool IsConfirmed { get; private set; }

    // Wired from the View to close the dialog.
    public System.Action? CloseAction { get; set; }

    public string TargetShortSha => TargetSha.Length >= 7 ? TargetSha.Substring(0, 7) : TargetSha;

    // Client-side gate for instant feedback; the service still re-validates the name
    // (never trust the UI — see T-05 plan §4.2).
    private bool CanConfirm => !string.IsNullOrWhiteSpace(TagName);

    partial void OnTagNameChanged(string value)
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
        IsConfirmed = false;
        CloseAction?.Invoke();
    }
}
