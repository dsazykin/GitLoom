using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;

namespace Mainguard.App.Shell.Views;

/// <summary>
/// The Client edition's dedicated "Clone" first-run window (1d, ADR-0001). A <see cref="ChromedWindow"/>
/// that hosts the REUSED Clone Dashboard behind a light "get your first repository" framing (see
/// ClientFirstRunWindow.axaml). It constructs NONE of the Pro MainguardOS surfaces. On completion — a repo
/// cloned/opened OR an explicit skip (<see cref="ClientFirstRunViewModel.Completed"/>) — it swaps the app
/// to the control-center shell (<see cref="MainWindow"/>), the SAME window-swap pattern the OOBE wizard and
/// the startup loader use.
/// </summary>
public partial class ClientFirstRunWindow : ChromedWindow
{
    public ClientFirstRunWindow()
    {
        InitializeComponent();
    }

    private ClientFirstRunViewModel? _boundVm;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_boundVm is not null)
            _boundVm.Completed -= OnCompletedSwap;

        _boundVm = DataContext as ClientFirstRunViewModel;
        if (_boundVm is not null)
            _boundVm.Completed += OnCompletedSwap;
    }

    // First run finished (repo cloned/opened or skipped): open the shell and close first-run.
    private void OnCompletedSwap(object? sender, EventArgs e)
    {
        var main = new MainWindow { DataContext = new MainWindowViewModel(null) };
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = main;

        main.Show();
        Close();
    }
}
