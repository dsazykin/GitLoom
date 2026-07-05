using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

// Engine-driven per-file resolver (T-04). Reads the three index stages via
// IGitService.GetConflictBlobs, chunks them with IMergeDiffService, and writes the
// assembled result back through ResolveConflict. No working-tree marker parsing,
// and the only disk writes happen in the service (MarkResolved / Keep / Delete).
//
// The view (ConflictResolverWindow) renders the chunks as a synchronized 3-pane
// merge editor (Ours | Result | Theirs). The chunk resolutions here are the source
// of truth for gating; on save the view supplies the exact merged text it displays
// via GetMergedText (falling back to AssembleMerged of the chunk models).
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

// Wraps one MergeChunk. "left" == ours, "right" == theirs.
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
    public string BaseText => Model.BaseText;
    public string OursText => Model.LeftText;     // "left" == ours
    public string TheirsText => Model.RightText;  // "right" == theirs

    // Non-conflict chunks are inherently resolved.
    public bool IsResolved => !IsConflict || Model.Resolution != ChunkResolution.Unresolved;

    public bool TookOurs => Model.Resolution == ChunkResolution.TakeLeft;
    public bool TookTheirs => Model.Resolution == ChunkResolution.TakeRight;
    public bool TookBoth => Model.Resolution == ChunkResolution.TakeBoth;
    public bool TookCustom => Model.Resolution == ChunkResolution.Custom;

    // The single effective text this chunk contributes to the Result pane, given its state.
    public string ResultText => Model.Kind switch
    {
        ChunkKind.Unchanged => Model.BaseText,
        ChunkKind.LeftOnly => Model.LeftText,
        ChunkKind.RightOnly => Model.RightText,
        ChunkKind.Conflict => Model.Resolution switch
        {
            ChunkResolution.TakeLeft => Model.LeftText,
            ChunkResolution.TakeRight => Model.RightText,
            ChunkResolution.TakeBoth => Combine(Model.LeftText, Model.RightText),
            ChunkResolution.Custom => Model.CustomText ?? "",
            _ => "",   // Unresolved -> empty in the Result pane
        },
        _ => Model.BaseText,
    };

    private static string Combine(string left, string right)
        => left.Length == 0 ? right : right.Length == 0 ? left : left + "\n" + right;

    [ObservableProperty] private string _customText = "";

    // Notifies IsResolved AND bubbles up so the parent recomputes gating (and the view rebuilds).
    public event Action? ResolutionChanged;

    [RelayCommand] private void TakeOurs() => SetResolution(ChunkResolution.TakeLeft);
    [RelayCommand] private void TakeTheirs() => SetResolution(ChunkResolution.TakeRight);
    [RelayCommand] private void TakeBoth() => SetResolution(ChunkResolution.TakeBoth);

    /// <summary>Records a free-form edit as the Custom resolution WITHOUT raising a rebuild
    /// (so the user's caret/typing is not disturbed). The view refreshes gating separately.</summary>
    public void SetCustomFromEditor(string text)
    {
        CustomText = text;
        Model.CustomText = text;
        Model.Resolution = ChunkResolution.Custom;
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(TookOurs));
        OnPropertyChanged(nameof(TookTheirs));
        OnPropertyChanged(nameof(TookBoth));
        OnPropertyChanged(nameof(TookCustom));
    }

    private void SetResolution(ChunkResolution r)
    {
        // v1 rule: a resolved chunk may switch sides, but never returns to Unresolved.
        Model.Resolution = r;
        OnPropertyChanged(nameof(IsResolved));
        OnPropertyChanged(nameof(TookOurs));
        OnPropertyChanged(nameof(TookTheirs));
        OnPropertyChanged(nameof(TookBoth));
        OnPropertyChanged(nameof(TookCustom));
        ResolutionChanged?.Invoke();
    }
}
