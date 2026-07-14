using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

/// <summary>
/// P2-48 in-app OOBE wizard window. Reuses MainWindow's custom client-area chrome (extended decorations
/// + a draggable title bar + the WindowButton controls) so setup looks like the same product, not a
/// separate dialog. On provisioning completion it swaps the app to the control center (MainWindow).
/// </summary>
public partial class OobeWizardView : Window
{
    public OobeWizardView()
    {
        InitializeComponent();
        UpdateMaximizeIcon();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateMaximizeIcon();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Auto-drive the OOBE state machine as soon as the window is up (or resume it after a reboot):
        // preflight diagnostics are read-only, and the machine pauses at the interactive consent gate.
        if (DataContext is OobeWizardViewModel vm && vm.StartCommand.CanExecute(null))
            vm.StartCommand.Execute(null);
    }

    private OobeWizardViewModel? _boundVm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_boundVm is not null)
            _boundVm.ProvisioningCompleted -= OnProvisioningCompleted;

        _boundVm = DataContext as OobeWizardViewModel;
        if (_boundVm is not null)
            _boundVm.ProvisioningCompleted += OnProvisioningCompleted;
    }

    // Provisioning finished and the user chose "Open GitLoom": open the control center and close setup.
    private void OnProvisioningCompleted(object? sender, EventArgs e)
    {
        var main = new MainWindow { DataContext = new MainWindowViewModel() };
        if (Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = main;
        }
        main.Show();
        Close();
    }

    // --- Custom title-bar chrome (mirrors MainWindow) ---

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void TitleBar_DoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    // "I'll restart later" — closes setup; the elevated resume Scheduled Task relaunches GitLoom back
    // into this wizard after the user restarts, continuing exactly where it left off.
    private void RestartLater_Click(object? sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon is null) return;
        var key = WindowState == WindowState.Maximized ? "WindowRestoreIcon" : "WindowMaximizeIcon";
        if (this.TryFindResource(key, out var res) && res is Avalonia.Media.Geometry geo)
            MaximizeIcon.Data = geo;
    }
}
