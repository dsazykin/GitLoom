using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Actions;

namespace GitLoom.App.ViewModels;

/// <summary>
/// One invokable candidate in the command palette (T-18): a registered <see cref="AppAction"/>, a branch, or
/// a bookmarked repository, flattened to a title + category + optional gesture + the delegate to run it.
/// </summary>
public sealed record PaletteEntry(string Title, string Category, string GestureText, Func<Task> Execute);

/// <summary>A run of a row's title, flagged when it is part of the fuzzy match (rendered emphasised).</summary>
public sealed record PaletteSegment(string Text, bool IsMatch);

/// <summary>
/// The Ctrl+P command palette (T-18). Type to fuzzy-filter registered actions + branch names + bookmarked
/// repos through the pure <see cref="FuzzyMatcher"/>; arrow keys move the selection, Enter invokes it. With
/// no query it browses everything grouped by category (header rows); with a query it shows a flat, ranked
/// list with the matched characters highlighted. All matching/ranking lives in Core — this VM only shapes
/// rows for the view.
/// </summary>
public partial class CommandPaletteViewModel : ViewModelBase
{
    private readonly Func<IReadOnlyList<PaletteEntry>> _entriesProvider;
    private IReadOnlyList<PaletteEntry> _snapshot = Array.Empty<PaletteEntry>();

    /// <summary>Raised when the palette should close (Escape, or after an action runs).</summary>
    public event Action? RequestClose;

    public CommandPaletteViewModel(Func<IReadOnlyList<PaletteEntry>> entriesProvider)
    {
        _entriesProvider = entriesProvider ?? throw new ArgumentNullException(nameof(entriesProvider));
    }

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private ObservableCollection<PaletteRowViewModel> _results = new();

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    private bool _hasNoResults;

    partial void OnQueryChanged(string value) => Rebuild();

    partial void OnSelectedIndexChanged(int oldValue, int newValue)
    {
        if (oldValue >= 0 && oldValue < Results.Count) Results[oldValue].IsSelected = false;
        if (newValue >= 0 && newValue < Results.Count) Results[newValue].IsSelected = true;
    }

    /// <summary>Snapshots the current candidate set and resets the query — call each time the palette opens so
    /// availability (CanExecute), branch list, and repos are fresh.</summary>
    public void Reset()
    {
        _snapshot = _entriesProvider();
        Query = string.Empty;
        Rebuild(); // OnQueryChanged won't fire if it was already empty
    }

    private void Rebuild()
    {
        var rows = new List<PaletteRowViewModel>();

        if (string.IsNullOrWhiteSpace(Query))
        {
            // Browse mode: group by category, ordered stably, with a header per group.
            foreach (var group in _snapshot
                         .Select((e, i) => (e, i))
                         .GroupBy(x => x.e.Category)
                         .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                rows.Add(PaletteRowViewModel.Header(group.Key));
                foreach (var (entry, _) in group.OrderBy(x => x.e.Title, StringComparer.OrdinalIgnoreCase))
                    rows.Add(PaletteRowViewModel.ForEntry(entry, BuildSegments(entry.Title, Array.Empty<int>())));
            }
        }
        else
        {
            // Query mode: flat, ranked by the pure matcher, matched chars highlighted.
            var ranked = FuzzyMatcher.Rank(Query, _snapshot, e => e.Title);
            foreach (var (entry, _) in ranked)
            {
                var positions = FuzzyMatcher.Match(Query, entry.Title).Positions;
                rows.Add(PaletteRowViewModel.ForEntry(entry, BuildSegments(entry.Title, positions)));
            }
        }

        Results = new ObservableCollection<PaletteRowViewModel>(rows);
        HasNoResults = !rows.Any(r => !r.IsHeader);
        SelectedIndex = FirstSelectableIndex();
        if (SelectedIndex >= 0) Results[SelectedIndex].IsSelected = true;
    }

    private static IReadOnlyList<PaletteSegment> BuildSegments(string title, IReadOnlyList<int> positions)
    {
        if (positions.Count == 0)
            return new[] { new PaletteSegment(title, false) };

        var set = new HashSet<int>(positions);
        var segments = new List<PaletteSegment>();
        var run = new System.Text.StringBuilder();
        bool? runMatch = null;
        for (int i = 0; i < title.Length; i++)
        {
            bool isMatch = set.Contains(i);
            if (runMatch is null || runMatch == isMatch)
            {
                run.Append(title[i]);
                runMatch = isMatch;
            }
            else
            {
                segments.Add(new PaletteSegment(run.ToString(), runMatch.Value));
                run.Clear();
                run.Append(title[i]);
                runMatch = isMatch;
            }
        }
        if (run.Length > 0 && runMatch is not null)
            segments.Add(new PaletteSegment(run.ToString(), runMatch.Value));
        return segments;
    }

    private int FirstSelectableIndex()
    {
        for (int i = 0; i < Results.Count; i++)
            if (!Results[i].IsHeader) return i;
        return -1;
    }

    [RelayCommand]
    private void MoveSelectionDown() => Move(+1);

    [RelayCommand]
    private void MoveSelectionUp() => Move(-1);

    private void Move(int direction)
    {
        if (Results.Count == 0) return;
        int i = SelectedIndex;
        for (int step = 0; step < Results.Count; step++)
        {
            i += direction;
            if (i < 0) i = Results.Count - 1;
            if (i >= Results.Count) i = 0;
            if (!Results[i].IsHeader)
            {
                SelectedIndex = i;
                return;
            }
        }
    }

    [RelayCommand]
    private async Task ExecuteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count) return;
        await Activate(Results[SelectedIndex]);
    }

    /// <summary>Runs a row's action (a no-op for headers) and requests close.</summary>
    public async Task Activate(PaletteRowViewModel? row)
    {
        if (row is null || row.IsHeader || row.Execute is null) return;
        RequestClose?.Invoke();
        await row.Execute();
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}

/// <summary>A single palette row: either a category header or an invokable entry with highlighted title segments.</summary>
public partial class PaletteRowViewModel : ViewModelBase
{
    public bool IsHeader { get; private init; }
    public string HeaderText { get; private init; } = string.Empty;
    public string Category { get; private init; } = string.Empty;
    public string GestureText { get; private init; } = string.Empty;
    public IReadOnlyList<PaletteSegment> Segments { get; private init; } = Array.Empty<PaletteSegment>();
    public Func<Task>? Execute { get; private init; }

    /// <summary>True when this entry has a keyboard gesture to show on the right edge.</summary>
    public bool HasGesture => !string.IsNullOrEmpty(GestureText);

    [ObservableProperty]
    private bool _isSelected;

    public static PaletteRowViewModel Header(string text) => new()
    {
        IsHeader = true,
        HeaderText = text,
    };

    public static PaletteRowViewModel ForEntry(PaletteEntry entry, IReadOnlyList<PaletteSegment> segments) => new()
    {
        IsHeader = false,
        Category = entry.Category,
        GestureText = entry.GestureText,
        Segments = segments,
        Execute = entry.Execute,
    };
}
