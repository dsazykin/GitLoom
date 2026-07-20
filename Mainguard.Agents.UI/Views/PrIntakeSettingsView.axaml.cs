using System;
using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

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
