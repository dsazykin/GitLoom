using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using GitLoom.App.ViewModels;
using Mainguard.Agents.Agents.Bootstrap;

namespace GitLoom.App.Views;

/// <summary>
/// The startup loading screen (owner design, 2026-07-17). Reuses MainWindow's client-area chrome so
/// setup looks like the same product, drives the <see cref="AppStartupSequence"/> as soon as it is
/// shown, and on completion swaps the app to the control center (MainWindow) — the same window-swap
/// pattern the OOBE wizard uses — carrying the degraded <see cref="StartupResult"/> into the shell's
/// banner.
/// </summary>
public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is StartupWindowViewModel vm)
        {
            _ = vm.StartAsync();
        }
    }

    private StartupWindowViewModel? _boundVm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_boundVm is not null)
        {
            _boundVm.Completed -= OnCompletedSwap;
        }

        _boundVm = DataContext as StartupWindowViewModel;
        if (_boundVm is not null)
        {
            _boundVm.Completed += OnCompletedSwap;
        }
    }

    // Startup finished: open the control center carrying the degraded result, then close the loader.
    private void OnCompletedSwap(object? sender, StartupResult result)
    {
        var main = new MainWindow { DataContext = new MainWindowViewModel(result) };
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = main;
        }

        main.Show();
        Close();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }
}
