using System;
using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

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
