using Avalonia.Controls;
using Avalonia.Interactivity;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

public partial class SettingsWindow : ChromedWindow
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Fetch the About/versions footer once per open (no polling; the VM stays inert until
        // now so tests and construction never touch the network). Refresh re-runs the same command.
        Opened += (_, _) =>
        {
            if (DataContext is ViewModels.SettingsViewModel vm)
            {
                vm.Versions.RefreshCommand.Execute(null);
            }
        };
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
