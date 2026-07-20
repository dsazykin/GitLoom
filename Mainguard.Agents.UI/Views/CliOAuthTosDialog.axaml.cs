using System;
using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

public partial class CliOAuthTosDialog : Window
{
    public CliOAuthTosDialog()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is CliOAuthTosDialogViewModel vm)
        {
            vm.CloseAction = _ => Close();
        }
    }
}
