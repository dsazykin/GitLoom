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
public partial class ConflictResolverWindowViewModel : ObservableObject
{
    private readonly IGitService _git;
    private readonly IMergeDiffService _merge;
    private readonly string _repoPath;
    private readonly string _path;

    /// <summary>Set by the opener so the VM can close the dialog with a DialogResult.</summary>
    public Window? Window { get; set; }

    public ObservableCollection<MergeChunkViewModel> Chunks { get; } = new();

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string _mergedPreview = "";
    [ObservableProperty] private bool _isBusy;

    // Keep the 3-column splitter layout of the original view.
    [ObservableProperty] private GridLength _column1Width = new(1, GridUnitType.Star);
    [ObservableProperty] private GridLength _column2Width = new(1, GridUnitType.Star);
    [ObservableProperty] private GridLength _column3Width = new(1, GridUnitType.Star);

    public bool HasOurs { get; }
    public bool HasTheirs { get; }

    /// <summary>Delete/modify or add/add with a missing stage: no chunk editor, offer Keep/Delete.</summary>
    public bool IsWholeFileMode => !HasOurs || !HasTheirs;
    public bool IsChunkMode => !IsWholeFileMode;
    public string MissingSideNote => !HasOurs ? "(deleted on our side)"
                                   : !HasTheirs ? "(deleted on their side)"
                                   : "";

    public string FileName => Path.GetFileName(_path);

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
            RecomputePreviewAndGating();
            IsLoading = false;
        });
    }

    private void OnAnyResolutionChanged() => RecomputePreviewAndGating();

    private void RecomputePreviewAndGating()
    {
        // Build a COPY where still-unresolved conflicts render as marker placeholders so the
        // preview is always assemble-able. This text is PREVIEW ONLY and never reaches disk.
        var preview = Chunks.Select(vm =>
        {
            var m = vm.Model;
            if (m.Kind == ChunkKind.Conflict && m.Resolution == ChunkResolution.Unresolved)
                return new MergeChunk
                {
                    Kind = ChunkKind.Unchanged,
                    BaseText = $"<<<<<<< ours\n{m.LeftText}\n=======\n{m.RightText}\n>>>>>>> theirs",
                };
            return m;
        });
        MergedPreview = _merge.AssembleMerged(preview);
        OnPropertyChanged(nameof(IsFullyResolved));
        MarkResolvedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(IsFullyResolved))]
    private async Task MarkResolved()
    {
        IsBusy = true;
        try
        {
            var merged = _merge.AssembleMerged(Chunks.Select(c => c.Model));
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

// Wraps one MergeChunk for the resolver list. "left" == ours, "right" == theirs.
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

    // For non-conflict chunks: the single effective text to render as context.
    public string ContextText => Model.Kind switch
    {
        ChunkKind.Unchanged => Model.BaseText,
        ChunkKind.LeftOnly => Model.LeftText,
        ChunkKind.RightOnly => Model.RightText,
        _ => Model.BaseText,
    };
    public bool IsAutoMerged => Model.Kind is ChunkKind.LeftOnly or ChunkKind.RightOnly;
    public string AutoMergedLabel => Model.Kind == ChunkKind.LeftOnly ? "auto-merged (ours)"
                                   : Model.Kind == ChunkKind.RightOnly ? "auto-merged (theirs)" : "";

    [ObservableProperty] private string _customText = "";

    // Notifies IsResolved AND bubbles up so the parent recomputes preview/gating.
    public event Action? ResolutionChanged;

    [RelayCommand] private void TakeOurs() => SetResolution(ChunkResolution.TakeLeft);
    [RelayCommand] private void TakeTheirs() => SetResolution(ChunkResolution.TakeRight);
    [RelayCommand] private void TakeBoth() => SetResolution(ChunkResolution.TakeBoth);
    [RelayCommand] private void UseCustom() { Model.CustomText = CustomText; SetResolution(ChunkResolution.Custom); }

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
