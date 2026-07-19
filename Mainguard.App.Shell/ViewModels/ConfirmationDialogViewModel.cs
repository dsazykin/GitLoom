using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

public partial class ConfirmationDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Confirm Action";

    [ObservableProperty]
    private string _message = "Are you sure you want to proceed?";

    [ObservableProperty]
    private string _confirmButtonText = "Confirm";

    [ObservableProperty]
    private string _optionalCheckboxText = string.Empty;

    [ObservableProperty]
    private bool _isOptionalCheckboxVisible = false;

    [ObservableProperty]
    private bool _isOptionalCheckboxChecked = false;

    public bool IsConfirmed { get; private set; }

    [RelayCommand]
    private void Confirm(Avalonia.Controls.Window window)
    {
        IsConfirmed = true;
        window.Close();
    }

    [RelayCommand]
    private void Cancel(Avalonia.Controls.Window window)
    {
        IsConfirmed = false;
        window.Close();
    }
}
