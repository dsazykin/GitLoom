using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.Views;

public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>Moves keyboard focus to the query box — the host calls this when the overlay opens.</summary>
    public void FocusInput()
    {
        var box = this.FindControl<TextBox>("QueryInput");
        box?.Focus();
        box?.SelectAll();
    }

    private void Row_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: PaletteRowViewModel row } && DataContext is CommandPaletteViewModel vm)
        {
            e.Handled = true;
            _ = vm.Activate(row);
        }
    }
}
