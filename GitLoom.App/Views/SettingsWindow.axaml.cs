using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GitLoom.App.Views;

public partial class SettingsWindow : ChromedWindow
{
    public SettingsWindow()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();
}
