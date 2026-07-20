using Avalonia.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

public partial class EgressAllowlistView : Window
{
    public EgressAllowlistView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is EgressAllowlistViewModel vm)
        {
            vm.CloseAction = Close;
        }
    }
}
