using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// One row in the Settings window's sidebar — a <c>RailSectionViewModel</c> shrunk to what a
/// Settings page needs: a stable id, a label/icon, and lazy-built content. Unlike the section
/// rail, every Settings page is always enabled (no <c>RequiresWorkspace</c>/adornment concept),
/// so this carries only what's actually used.
/// </summary>
public partial class SettingsPageRowViewModel : ViewModelBase
{
    private readonly Func<object> _buildContent;
    private object? _content;

    public SettingsPageRowViewModel(string id, string label, string iconResourceKey, Func<object> buildContent, Action<string> activate)
    {
        Id = id;
        Label = label;
        IconResourceKey = iconResourceKey;
        _buildContent = buildContent;
        ActivateCommand = new RelayCommand(() => activate(Id));
    }

    public string Id { get; }
    public string Label { get; }
    public string IconResourceKey { get; }

    [ObservableProperty] private bool _isActive;

    public IRelayCommand ActivateCommand { get; }

    /// <summary>Lazily builds (and caches) this page's content ViewModel on first activation.
    /// Daemon Logs overrides this caching by rebuilding fresh every activation — see
    /// <see cref="Rebuild"/>.</summary>
    public object Content => _content ??= _buildContent();

    /// <summary>Forces the next <see cref="Content"/> access to rebuild rather than reuse the
    /// cached instance — needed for pages (Daemon Logs) whose content is disposed on deactivate.</summary>
    public void Rebuild() => _content = null;
}
