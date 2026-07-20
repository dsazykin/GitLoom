using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.App.Shell.ViewModels;

// Engine-driven per-file resolver (T-04). Reads the three index stages via
// IGitService.GetConflictBlobs, chunks them with IMergeDiffService, and writes the
// assembled result back through ResolveConflict. No working-tree marker parsing,
// and the only disk writes happen in the service (MarkResolved / Keep / Delete).
//
// The view (ConflictResolverWindow) renders the chunks as a synchronized 3-pane
// merge editor (Ours | Result | Theirs). Each conflict tracks per-side accept/reject
// state so both sides can be taken together, either can be denied, and choices undone.
public partial class ConflictResolverWindowViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IMergeDiffService _merge;
    private readonly string _repoPath;
    private readonly string _path;

    /// <summary>Set by the opener so the VM can close the dialog with a DialogResult.</summary>
    public Window? Window { get; set; }

    /// <summary>Supplied by the view: returns the exact text shown in the editable Result pane
    /// (filler lines stripped). Falls back to AssembleMerged over the chunk models.</summary>
    public Func<string>? GetMergedText { get; set; }

    /// <summary>Raised on the UI thread after chunks are loaded so the view builds its documents.</summary>
    public event Action? ChunksReady;

    public ObservableCollection<MergeChunkViewModel> Chunks { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isBusy;

    public bool HasOurs { get; }
    public bool HasTheirs { get; }

    /// <summary>Delete/modify or add/add with a missing stage: no chunk editor, offer Keep/Delete.</summary>
    public bool IsWholeFileMode => !HasOurs || !HasTheirs;
    public bool IsChunkMode => !IsWholeFileMode;
    public string MissingSideNote => !HasOurs ? "(deleted on our side)"
                                   : !HasTheirs ? "(deleted on their side)"
                                   : "";

    public string FileName => Path.GetFileName(_path);

    public int ConflictCount => Chunks.Count(c => c.IsConflict);
    public int ResolvedConflictCount => Chunks.Count(c => c.IsConflict && c.IsResolved);
    public string StatusText => ConflictCount == 0
        ? "No conflicts"
        : $"{ResolvedConflictCount} of {ConflictCount} conflicts resolved";

    public bool IsFullyResolved => !IsWholeFileMode && Chunks.All(c => c.IsResolved);

    public ConflictResolverWindowViewModel(
        IGitService git, IMergeDiffService merge, string repoPath, string conflictedPath,
        bool hasOurs, bool hasTheirs)
    {
        _git = git;
        _merge = merge;
        _repoPath = repoPath;
        _path = conflictedPath;
        HasOurs = hasOurs;
        HasTheirs = hasTheirs;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        if (IsWholeFileMode)
        {
            // No chunking for a missing-stage file — the surviving side / delete are the only options.
            IsLoading = false;
            OnPropertyChanged(nameof(IsChunkMode));
            return;
        }

        var chunks = await Task.Run(() =>
        {
            var (b, o, t) = _git.GetConflictBlobs(_repoPath, _path);
            return _merge.GenerateMergeChunks(b, o, t);
        });

        // Bound collections are touched only on the UI thread (invariant 3).
        Dispatcher.UIThread.Post(() =>
        {
            Chunks.Clear();
            foreach (var c in chunks)
            {
                var vm = new MergeChunkViewModel(c);
                vm.ResolutionChanged += OnAnyResolutionChanged;
                Chunks.Add(vm);
            }
            RecomputeGating();
            IsLoading = false;
            ChunksReady?.Invoke();
        });
    }

    private void OnAnyResolutionChanged() => RecomputeGating();

    /// <summary>Called by the view when the user types a resolution into a conflict region
    /// (no document rebuild — just refresh gating/status).</summary>
    public void NotifyResolvedByEdit() => RecomputeGating();

    private void RecomputeGating()
    {
        OnPropertyChanged(nameof(IsFullyResolved));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ResolvedConflictCount));
        MarkResolvedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsFullyResolved))]
    private async Task MarkResolved()
    {
        IsBusy = true;
        try
        {
            var merged = GetMergedText?.Invoke() ?? _merge.AssembleMerged(Chunks.Select(c => c.Model));
            await Task.Run(() => _git.ResolveConflict(_repoPath, _path, merged));
            Window?.Close(true);
        }
        finally { IsBusy = false; }
    }

    // Whole-file (delete/modify) actions ---------------------------------------

    [RelayCommand]
    private async Task KeepFile()
    {
        IsBusy = true;
        try
        {
            var survivingSide = HasOurs ? ConflictSide.Ours : ConflictSide.Theirs;
            await Task.Run(() => _git.ResolveFileWithSide(_repoPath, _path, survivingSide));
            Window?.Close(true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteFile()
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _git.RemoveFileFromMerge(_repoPath, _path));
            Window?.Close(true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Cancel() => Window?.Close(false);
}

public enum SideChoice { Undecided, Accepted, Rejected }

// Wraps one MergeChunk. "left" == ours, "right" == theirs. For a conflict, each side is
// independently Accepted / Rejected / Undecided; the resolution is derived from those.
public partial class MergeChunkViewModel : ObservableObject
{
    public MergeChunk Model { get; }

    public MergeChunkViewModel(MergeChunk model)
    {
        Model = model;
        CustomText = model.CustomText ?? "";
    }

    public ChunkKind Kind => Model.Kind;
    public bool IsConflict => Model.Kind == ChunkKind.Conflict;
    // add/add: both sides added different text where the base had nothing (no common line modified).
    public bool IsAddConflict => IsConflict && Model.BaseText.Length == 0;
    public string BaseText => Model.BaseText;
    public string OursText => Model.LeftText;     // "left" == ours
    public string TheirsText => Model.RightText;  // "right" == theirs

    public SideChoice OursChoice { get; private set; } = SideChoice.Undecided;
    public SideChoice TheirsChoice { get; private set; } = SideChoice.Undecided;

    private bool _manualResolved;

    /// <summary>True once the Result region was hand-edited, so the view should stop
    /// laying it out as stacked accept/reject slots and honor the typed text verbatim.</summary>
    public bool IsManuallyEdited => _manualResolved;

    // Non-conflict chunks are inherently resolved. A conflict is resolved once at least one side is
    // accepted (accept-one = take that side), both are explicitly rejected (delete the region), or
    // the Result pane was hand-edited.
    public bool IsResolved => !IsConflict || _manualResolved
        || OursChoice == SideChoice.Accepted || TheirsChoice == SideChoice.Accepted
        || (OursChoice == SideChoice.Rejected && TheirsChoice == SideChoice.Rejected);

    // What this chunk contributes to the Result pane — reflects accepted sides live, so accepting one
    // side updates the middle immediately (even before the other side is decided).
    public string ResultText => Model.Kind switch
    {
        ChunkKind.Unchanged => Model.BaseText,
        ChunkKind.LeftOnly => Model.LeftText,
        ChunkKind.RightOnly => Model.RightText,
        ChunkKind.Conflict => _manualResolved ? (Model.CustomText ?? "") : AcceptedText(),
        _ => Model.BaseText,
    };

    private string AcceptedText()
    {
        var parts = new List<string>();
        if (OursChoice == SideChoice.Accepted) parts.Add(OursText);
        if (TheirsChoice == SideChoice.Accepted) parts.Add(TheirsText);
        return string.Join("\n", parts);
    }

    [ObservableProperty] private string _customText = "";

    // Raised so the view rebuilds its documents and the parent recomputes gating.
    public event Action? ResolutionChanged;

    // --- Interactive per-side toggles (click again to undo) ---
    public void ToggleAcceptOurs() { OursChoice = OursChoice == SideChoice.Accepted ? SideChoice.Undecided : SideChoice.Accepted; AfterChoiceChange(); }
    public void ToggleRejectOurs() { OursChoice = OursChoice == SideChoice.Rejected ? SideChoice.Undecided : SideChoice.Rejected; AfterChoiceChange(); }
    public void ToggleAcceptTheirs() { TheirsChoice = TheirsChoice == SideChoice.Accepted ? SideChoice.Undecided : SideChoice.Accepted; AfterChoiceChange(); }
    public void ToggleRejectTheirs() { TheirsChoice = TheirsChoice == SideChoice.Rejected ? SideChoice.Undecided : SideChoice.Rejected; AfterChoiceChange(); }

    // --- Bulk resolve (All Ours / All Theirs) ---
    public void ForceOurs() { OursChoice = SideChoice.Accepted; TheirsChoice = SideChoice.Rejected; AfterChoiceChange(); }
    public void ForceTheirs() { OursChoice = SideChoice.Rejected; TheirsChoice = SideChoice.Accepted; AfterChoiceChange(); }

    private void AfterChoiceChange()
    {
        _manualResolved = false;
        CustomText = AcceptedText();               // accepted sides, ours before theirs
        Model.CustomText = CustomText;
        Model.Resolution = IsResolved ? ChunkResolution.Custom : ChunkResolution.Unresolved;
        Raise();
    }

    /// <summary>Records a free-form edit as the resolution WITHOUT raising a rebuild
    /// (so the user's caret/typing is not disturbed). The view refreshes gating separately.</summary>
    public void SetCustomFromEditor(string text)
    {
        _manualResolved = true;
        CustomText = text;
        Model.CustomText = text;
        Model.Resolution = ChunkResolution.Custom;
        OnPropertyChanged(nameof(IsResolved));
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(OursChoice));
        OnPropertyChanged(nameof(TheirsChoice));
        OnPropertyChanged(nameof(ResultText));
        ResolutionChanged?.Invoke();
    }
}
