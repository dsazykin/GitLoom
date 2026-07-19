using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

public partial class ShortcutSettingsWindow : ChromedWindow
{
    public ShortcutSettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ShortcutSettingsViewModel vm)
                vm.RequestClose += Close;
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
