using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class ApiKeySettingsView : Window
{
    public ApiKeySettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is ApiKeySettingsViewModel vm)
        {
            vm.CloseAction = Close;
            vm.ShowTosDialogAsync = ShowTosDialogAsync;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        // Cancel any in-flight health check when the page closes (P2-01 §4.1).
        (DataContext as ApiKeySettingsViewModel)?.CancelPendingWork();
    }

    private async Task<bool> ShowTosDialogAsync(string provider)
    {
        var vm = new CliOAuthTosDialogViewModel(provider);
        var dialog = new CliOAuthTosDialog { DataContext = vm };
        await dialog.ShowDialog(this);
        return vm.Acknowledged;
    }
}
