using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>One pinnable-menu row in the Settings window (#78): its label + a checkbox-backed pin toggle.</summary>
public partial class SettingsPinRowViewModel : ViewModelBase
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isPinned;
}

/// <summary>File → Settings… (#78): the pinned top-menu-icons picker plus the read-only
/// "About / versions" footer (app, daemon, GitLoomOS payload — see <see cref="VersionsViewModel"/>).</summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;
    private readonly Action _onPinsChanged;

    public ObservableCollection<SettingsPinRowViewModel> PinRows { get; } = new();

    /// <summary>The versions footer. Inert until the window's <c>Opened</c> hook (or the user's
    /// Refresh) fires its fetch, so constructing this VM never touches the network.</summary>
    public VersionsViewModel Versions { get; }

    public SettingsViewModel(
        ISettingsService settingsService, Action onPinsChanged, VersionsViewModel? versions = null)
    {
        _settingsService = settingsService;
        _onPinsChanged = onPinsChanged;
        Versions = versions ?? new VersionsViewModel();

        var pinned = _settingsService.Current.PinnedMenuIds;
        foreach (var def in PinnableMenus.All)
        {
            var row = new SettingsPinRowViewModel { Id = def.Id, Label = def.Label, IsPinned = pinned.Contains(def.Id) };
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SettingsPinRowViewModel.IsPinned)) TogglePin(row);
            };
            PinRows.Add(row);
        }
    }

    private void TogglePin(SettingsPinRowViewModel row)
    {
        _settingsService.Update(p =>
        {
            if (row.IsPinned)
            {
                if (!p.PinnedMenuIds.Contains(row.Id)) p.PinnedMenuIds.Add(row.Id);
            }
            else
            {
                p.PinnedMenuIds.Remove(row.Id);
            }
        });
        _onPinsChanged();
    }
}
