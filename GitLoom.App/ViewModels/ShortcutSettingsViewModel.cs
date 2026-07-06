using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Actions;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The keyboard-shortcut rebind screen (T-18). Lists every registered action with its current gesture,
/// recomputes conflicts live via the pure <see cref="ShortcutMap"/>, and persists only the overrides that
/// differ from the built-in defaults. Conflicting rows are flagged and a warning is surfaced so the user
/// can still save but knows two actions share a gesture.
/// </summary>
public partial class ShortcutSettingsViewModel : ViewModelBase
{
    private readonly Action<Dictionary<string, string>> _save;

    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    private bool _hasConflicts;

    [ObservableProperty]
    private string _conflictMessage = string.Empty;

    public event Action? RequestClose;

    public ShortcutSettingsViewModel(
        ShortcutMap current,
        IEnumerable<(string Id, string Title)> actions,
        Action<Dictionary<string, string>> save)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));

        foreach (var (id, title) in actions)
        {
            var row = new ShortcutRowViewModel(id, title) { Gesture = current.GestureFor(id) ?? string.Empty };
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        Recompute();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShortcutRowViewModel.Gesture))
            Recompute();
    }

    private ShortcutMap BuildMap() =>
        new(Rows.Where(r => !string.IsNullOrWhiteSpace(r.Gesture))
                .Select(r => new KeyValuePair<string, string>(r.Id, r.Gesture.Trim())));

    private void Recompute()
    {
        var map = BuildMap();
        var conflicts = map.Conflicts();
        var conflictedIds = new HashSet<string>(conflicts.SelectMany(c => c.ActionIds), StringComparer.Ordinal);

        foreach (var row in Rows)
            row.IsConflicting = conflictedIds.Contains(row.Id);

        HasConflicts = conflicts.Count > 0;
        ConflictMessage = HasConflicts
            ? "Conflicting shortcut(s): " + string.Join(", ", conflicts.Select(c => c.Gesture))
            : string.Empty;
    }

    [RelayCommand]
    private void Save()
    {
        _save(BuildMap().ToPreferences());
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        var defaults = ShortcutMap.Default;
        foreach (var row in Rows)
            row.Gesture = defaults.GestureFor(row.Id) ?? string.Empty;
        Recompute();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();
}

/// <summary>One rebindable action row: its id, title, editable gesture, and a live conflict flag.</summary>
public partial class ShortcutRowViewModel : ViewModelBase
{
    public string Id { get; }
    public string Title { get; }

    [ObservableProperty]
    private string _gesture = string.Empty;

    [ObservableProperty]
    private bool _isConflicting;

    public ShortcutRowViewModel(string id, string title)
    {
        Id = id;
        Title = title;
    }
}
