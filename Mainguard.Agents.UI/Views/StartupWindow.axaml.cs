using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

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
        // Build the shell through the composition seam (step 2e): MainWindow/MainWindowViewModel stay in
        // the shell, which this Pro-only assembly must not reference — the shell wires CreateShellWindow.
        var main = ProComposition.CreateShellWindow?.Invoke(result);
        if (main is null)
        {
            return;
        }

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
