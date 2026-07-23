using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

/// <summary>Settings → AI Providers, embedded as a page (was a standalone dialog).</summary>
public partial class ApiKeySettingsView : UserControl
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
            vm.ShowTosDialogAsync = ShowTosDialogAsync;
        }
    }

    private async Task<bool> ShowTosDialogAsync(string provider)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var vm = new CliOAuthTosDialogViewModel(provider);
        var dialog = new CliOAuthTosDialog { DataContext = vm };
        if (owner is not null)
            await dialog.ShowDialog(owner);
        return vm.Acknowledged;
    }
}
