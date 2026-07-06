using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class ShortcutSettingsWindow : Window
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
