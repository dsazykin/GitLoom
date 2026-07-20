using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

public enum CheckoutConflictResult
{
    Cancel,
    Stash,
    CarryOver
}

public partial class CheckoutConflictDialogViewModel : ViewModelBase
{
    public CheckoutConflictResult Result { get; private set; } = CheckoutConflictResult.Cancel;

    [RelayCommand]
    private void Stash(Avalonia.Controls.Window window)
    {
        Result = CheckoutConflictResult.Stash;
        window.Close();
    }

    [RelayCommand]
    private void CarryOver(Avalonia.Controls.Window window)
    {
        Result = CheckoutConflictResult.CarryOver;
        window.Close();
    }

    [RelayCommand]
    private void Cancel(Avalonia.Controls.Window window)
    {
        Result = CheckoutConflictResult.Cancel;
        window.Close();
    }
}
