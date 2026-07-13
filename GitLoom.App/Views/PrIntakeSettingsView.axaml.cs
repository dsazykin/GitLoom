using System;
using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class PrIntakeSettingsView : Window
{
    public PrIntakeSettingsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is PrIntakeSettingsViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
