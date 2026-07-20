using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// Operation-history panel (T-19): lists the repository's journaled operations newest-first
/// and drives per-entry Undo / Redo through <see cref="IOperationJournal"/>. Non-undoable
/// entries (push, stash pop) show a disabled Undo with their blocked reason as a tooltip.
/// Hosted by OperationHistoryWindow.
/// </summary>
public partial class OperationHistoryViewModel : ViewModelBase
{
    private readonly IOperationJournal _journal;
    private readonly string _repoPath;
    private readonly Action? _onChanged;

    public ObservableCollection<OperationHistoryRowViewModel> Entries { get; } = new();

    [ObservableProperty]
    private string? _errorMessage;

    public bool HasEntries => Entries.Count > 0;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public OperationHistoryViewModel(IOperationJournal journal, string repoPath, Action? onChanged = null)
    {
        _journal = journal;
        _repoPath = repoPath;
        _onChanged = onChanged;
        Reload();
    }

    private void Reload()
    {
        Entries.Clear();
        foreach (var e in _journal.GetHistory(_repoPath))
            Entries.Add(new OperationHistoryRowViewModel(e, this));
        OnPropertyChanged(nameof(HasEntries));
    }

    internal void Undo(OperationHistoryRowViewModel row)
        => Run(() => _journal.Undo(_repoPath, row.Id));

    internal void Redo(OperationHistoryRowViewModel row)
        => Run(() => _journal.Redo(_repoPath, row.Id));

    // Runs an undo/redo, funnels any typed failure into ErrorMessage, refreshes the
    // workspace on success, and reloads the list either way (state may have changed).
    private void Run(Action mutate)
    {
        try
        {
            mutate();
            ErrorMessage = null;
            _onChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            Reload();
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One journaled operation row: kind/description/time plus its undo/redo affordances.</summary>
public partial class OperationHistoryRowViewModel : ViewModelBase
{
    private readonly OperationHistoryViewModel _parent;

    public long Id { get; }
    public string Kind { get; }
    public string Description { get; }
    public string When { get; }
    public bool IsUndone { get; }
    public bool IsUndoable { get; }
    public bool IsTruncated { get; }
    public string? UndoBlockedReason { get; }

    public OperationHistoryRowViewModel(JournalEntry entry, OperationHistoryViewModel parent)
    {
        _parent = parent;
        Id = entry.Id;
        Kind = entry.Kind;
        Description = entry.Description;
        When = entry.WhenUtc.ToLocalTime().ToString("MMM d, HH:mm");
        IsUndone = entry.IsUndone;
        IsUndoable = entry.IsUndoable;
        IsTruncated = entry.IsTruncated;
        UndoBlockedReason = entry.UndoBlockedReason;
    }

    // Undo is offered for an undoable op that is currently applied; Redo for one that was undone
    // and has not been superseded (truncated) by a newer operation.
    public bool CanUndo => IsUndoable && !IsUndone;
    public bool CanRedo => IsUndoable && IsUndone && !IsTruncated;

    // Status chip text for the row's current state.
    public string StatusText => !IsUndoable ? "Not undoable" : IsTruncated ? "Superseded" : IsUndone ? "Undone" : "Applied";

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo() => _parent.Undo(this);

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo() => _parent.Redo(this);
}
