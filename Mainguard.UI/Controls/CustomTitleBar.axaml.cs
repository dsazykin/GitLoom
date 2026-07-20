using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitLoom.App.Views;

namespace GitLoom.App.Controls;

public partial class CustomTitleBar : UserControl
{
    public CustomTitleBar()
    {
        InitializeComponent();
    }

    private ChromedWindow? Host => this.VisualRoot as ChromedWindow;

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e) => Host?.BeginTitleBarDrag(e);

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) => Host?.ToggleMaximizeFromTitleBar();

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (Host is { } host) host.WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) => Host?.ToggleMaximizeFromTitleBar();

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Host?.Close();
}
