using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class DiffViewerViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly System.Action? _onStagingChanged;

    // Partial-staging state (T-06). The parsed patch is the source of truth the builder
    // subsets from; direction follows the file's staged state (workdir<->index vs index<->HEAD).
    private GitFileStatus? _currentFile;
    private GitLoom.Core.Models.FilePatch? _currentPatch;

    [ObservableProperty]
    private ObservableCollection<GitDiffLine> _diffLines = new();

    [ObservableProperty]
    private bool _isSideBySideView;
    partial void OnIsSideBySideViewChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffViewModeText));
        UpdateVisibility();
    }

    [ObservableProperty]
    private bool _isEditMode;
    partial void OnIsEditModeChanged(bool value)
    {
        UpdateVisibility();
    }

    [ObservableProperty]
    private bool _showUnified = true;

    [ObservableProperty]
    private bool _showSideBySide = false;

    private void UpdateVisibility()
    {
        ShowUnified = !IsSideBySideView && !IsEditMode;
        ShowSideBySide = IsSideBySideView && !IsEditMode;
    }

    public string DiffViewModeText => IsSideBySideView ? "Show Unified Diff" : "Show Split Diff";

    [RelayCommand]
    private void ToggleDiffView()
    {
        IsSideBySideView = !IsSideBySideView;
    }

    [ObservableProperty]
    private string _rawContent = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private string _filePath = string.Empty;

    public bool HasFile => !string.IsNullOrEmpty(FilePath);

    [ObservableProperty]
    private bool _hasConflicts;

    [ObservableProperty]
    private int _conflictCount;

    public System.Collections.Generic.HashSet<int> AddedLines { get; } = new();
    public System.Collections.Generic.HashSet<int> ModifiedLines { get; } = new();

    [RelayCommand]
    private async System.Threading.Tasks.Task Open3WayResolverAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            // libgit2 reports conflict paths with forward slashes; match on that form.
            var relPath = FilePath.Replace('\\', '/');
            var conflict = System.Linq.Enumerable.FirstOrDefault(
                _gitService.GetConflicts(_repoPath), c => c.Path == relPath);

            var dialog = new Views.ConflictResolverWindow();
            var vm = new ConflictResolverWindowViewModel(
                _gitService, new MergeDiffService(), _repoPath, relPath,
                conflict?.HasOurs ?? true, conflict?.HasTheirs ?? true)
            { Window = dialog };
            dialog.DataContext = vm;

            var result = await dialog.ShowDialog<bool>(desktop.MainWindow);
            if (result)
            {
                // ResolveConflict already staged the file; refresh the UI to show the new state.
                var status = new GitFileStatus { FilePath = FilePath, State = LibGit2Sharp.FileStatus.ModifiedInIndex };
                UpdateDiff(status);
            }
        }
    }

    private void CheckForConflicts()
    {
        if (string.IsNullOrEmpty(RawContent))
        {
            HasConflicts = false;
            ConflictCount = 0;
            return;
        }

        var regex = new System.Text.RegularExpressions.Regex(@"<<<<<<<.*?\n");
        var matches = regex.Matches(RawContent);
        ConflictCount = matches.Count;
        HasConflicts = ConflictCount > 0;

        // Auto-switch to edit mode if there are conflicts to resolve
        if (HasConflicts && !IsEditMode)
        {
            IsEditMode = true;
        }
    }

    [RelayCommand]
    private void SaveFile()
    {
        if (!string.IsNullOrEmpty(FilePath))
        {
            var fullPath = System.IO.Path.Combine(_repoPath, FilePath);
            try
            {
                System.IO.File.WriteAllText(fullPath, RawContent);
            }
            catch { }
        }
    }

    [ObservableProperty]
    private ObservableCollection<SideBySideDiffRow> _sideBySideLines = new();

    // Structured hunks for partial staging (T-06). Rendered in the unified view.
    [ObservableProperty]
    private ObservableCollection<DiffHunkRowViewModel> _hunks = new();

    [ObservableProperty]
    private bool _hasSelectedLines;

    // True when viewing the staged (index<->HEAD) diff → actions unstage rather than stage/discard.
    [ObservableProperty]
    private bool _isStagedView;

    [ObservableProperty]
    private string? _partialStagingError;

    [ObservableProperty]
    private bool _isBusy;

    public DiffViewerViewModel(IGitService gitService, string repoPath, System.Action? onStagingChanged = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onStagingChanged = onStagingChanged;
    }

    public void UpdateDiff(GitFileStatus? file)
    {
        if (file == null)
        {
            DiffLines.Clear();
            SideBySideLines.Clear();
            Hunks.Clear();
            _currentFile = null;
            _currentPatch = null;
            HasSelectedLines = false;
            PartialStagingError = null;
            RawContent = string.Empty;
            FilePath = string.Empty;
            return;
        }

        _currentFile = file;
        IsStagedView = file.IsStaged;
        PartialStagingError = null;
        FilePath = file.FilePath;
        var fullPath = System.IO.Path.Combine(_repoPath, FilePath);
        if (System.IO.File.Exists(fullPath))
        {
            RawContent = System.IO.File.ReadAllText(fullPath);
        }
        else
        {
            RawContent = string.Empty;
        }

        // CheckForConflicts already flips IsEditMode on when markers are present.
        CheckForConflicts();

        var rawDiff = _gitService.GetFileDiff(_repoPath, file.FilePath, file.IsStaged);

        var unifiedLines = new ObservableCollection<GitDiffLine>();
        var sbsLines = new ObservableCollection<SideBySideDiffRow>();

        AddedLines.Clear();
        ModifiedLines.Clear();

        if (!string.IsNullOrEmpty(rawDiff))
        {
            var lines = rawDiff.Split('\n');
            int i = 0;

            int currentNewLine = 0;

            while (i < lines.Length)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) { i++; continue; }

                var diffLine = new GitDiffLine { LineType = line[0], Content = line };
                unifiedLines.Add(diffLine);

                char type = line[0];
                if (type == '@')
                {
                    // Parse @@ -old,old_count +new,new_count @@
                    var match = System.Text.RegularExpressions.Regex.Match(line, @"\+([0-9]+)");
                    if (match.Success)
                    {
                        currentNewLine = int.Parse(match.Groups[1].Value);
                    }
                    sbsLines.Add(new SideBySideDiffRow { LeftLine = diffLine, RightLine = diffLine });
                    i++;
                }
                else if (type == ' ')
                {
                    currentNewLine++;
                    sbsLines.Add(new SideBySideDiffRow { LeftLine = diffLine, RightLine = diffLine });
                    i++;
                }
                else if (type == '-' || type == '+')
                {
                    var deletions = new System.Collections.Generic.List<GitDiffLine>();
                    var additions = new System.Collections.Generic.List<GitDiffLine>();

                    while (i < lines.Length && !string.IsNullOrEmpty(lines[i]) && (lines[i][0] == '-' || lines[i][0] == '+'))
                    {
                        var chunkLine = new GitDiffLine { LineType = lines[i][0], Content = lines[i] };
                        unifiedLines.Add(chunkLine);

                        if (lines[i][0] == '-') deletions.Add(chunkLine);
                        else additions.Add(chunkLine);

                        i++;
                    }

                    int maxRows = System.Math.Max(deletions.Count, additions.Count);
                    var emptyLine = new GitDiffLine { LineType = ' ', Content = "" };

                    bool isModification = deletions.Count > 0 && additions.Count > 0;

                    for (int j = 0; j < additions.Count; j++)
                    {
                        if (isModification) ModifiedLines.Add(currentNewLine);
                        else AddedLines.Add(currentNewLine);
                        currentNewLine++;
                    }

                    for (int j = 0; j < maxRows; j++)
                    {
                        sbsLines.Add(new SideBySideDiffRow
                        {
                            LeftLine = j < deletions.Count ? deletions[j] : emptyLine,
                            RightLine = j < additions.Count ? additions[j] : emptyLine
                        });
                    }
                }
                else
                {
                    i++;
                }
            }
        }

        DiffLines = unifiedLines;
        SideBySideLines = sbsLines;

        BuildHunks(rawDiff, file.IsStaged);
    }

    // ---- Partial staging (T-06) --------------------------------------------

    private void BuildHunks(string rawDiff, bool isStaged)
    {
        var hunks = new ObservableCollection<DiffHunkRowViewModel>();
        _currentPatch = null;
        HasSelectedLines = false;

        if (!string.IsNullOrEmpty(rawDiff))
        {
            // Exactly one FilePatch per diff-viewer (a single file is shown at a time).
            var file = System.Linq.Enumerable.FirstOrDefault(GitLoom.Core.Services.PatchParser.Parse(rawDiff));
            if (file != null)
            {
                _currentPatch = file;
                for (int h = 0; h < file.Hunks.Count; h++)
                {
                    var hunk = file.Hunks[h];
                    var row = new DiffHunkRowViewModel
                    {
                        HunkIndex = h,
                        HeaderText = HunkHeaderText(hunk),
                        IsStaged = isStaged
                    };
                    for (int i = 0; i < hunk.Lines.Count; i++)
                    {
                        var line = hunk.Lines[i];
                        row.Lines.Add(new DiffLineRowViewModel
                        {
                            IndexInHunk = i,
                            Kind = line.Kind,
                            DisplayText = PrefixOf(line.Kind) + line.Text,
                            OnSelectionChanged = RecomputeSelection
                        });
                    }
                    FillSideRows(row, hunk);
                    hunks.Add(row);
                }
            }
        }

        Hunks = hunks;
    }

    // Pairs a hunk's deletes/adds into old|new rows (deletes left, adds right, filler where
    // one side is shorter) for the block-level side-by-side view.
    private static void FillSideRows(DiffHunkRowViewModel row, GitLoom.Core.Models.DiffHunk hunk)
    {
        var empty = new GitDiffLine { LineType = ' ', Content = "" };
        var dels = new System.Collections.Generic.List<GitDiffLine>();
        var adds = new System.Collections.Generic.List<GitDiffLine>();

        void Flush()
        {
            int max = System.Math.Max(dels.Count, adds.Count);
            for (int j = 0; j < max; j++)
            {
                row.SideRows.Add(new SideBySideDiffRow
                {
                    LeftLine = j < dels.Count ? dels[j] : empty,
                    RightLine = j < adds.Count ? adds[j] : empty
                });
            }
            dels.Clear();
            adds.Clear();
        }

        foreach (var line in hunk.Lines)
        {
            switch (line.Kind)
            {
                case GitLoom.Core.Models.DiffLineKind.Context:
                    Flush();
                    var ctx = new GitDiffLine { LineType = ' ', Content = " " + line.Text };
                    row.SideRows.Add(new SideBySideDiffRow { LeftLine = ctx, RightLine = ctx });
                    break;
                case GitLoom.Core.Models.DiffLineKind.Delete:
                    dels.Add(new GitDiffLine { LineType = '-', Content = "-" + line.Text });
                    break;
                case GitLoom.Core.Models.DiffLineKind.Add:
                    adds.Add(new GitDiffLine { LineType = '+', Content = "+" + line.Text });
                    break;
            }
        }
        Flush();
    }

    private static string HunkHeaderText(GitLoom.Core.Models.DiffHunk h)
    {
        var oldSpan = h.OldCountOmitted ? $"{h.OldStart}" : $"{h.OldStart},{h.OldCount}";
        var newSpan = h.NewCountOmitted ? $"{h.NewStart}" : $"{h.NewStart},{h.NewCount}";
        return $"@@ -{oldSpan} +{newSpan} @@{h.SectionHeading}";
    }

    private static char PrefixOf(GitLoom.Core.Models.DiffLineKind kind) => kind switch
    {
        GitLoom.Core.Models.DiffLineKind.Add => '+',
        GitLoom.Core.Models.DiffLineKind.Delete => '-',
        _ => ' '
    };

    private void RecomputeSelection()
        => HasSelectedLines = System.Linq.Enumerable.Any(Hunks, h => System.Linq.Enumerable.Any(h.Lines, l => l.IsSelected));

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var hunk in Hunks)
            foreach (var line in hunk.Lines)
                line.IsSelected = false;
        HasSelectedLines = false;
    }

    [RelayCommand]
    private System.Threading.Tasks.Task StageHunk(int hunkIndex)
        => ApplyPartialAsync(() => _gitService.StageHunk(_repoPath, BuildHunkPatch(hunkIndex)), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task UnstageHunk(int hunkIndex)
        => ApplyPartialAsync(() => _gitService.UnstageHunk(_repoPath, BuildHunkPatch(hunkIndex)), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task DiscardHunk(int hunkIndex)
        => ApplyPartialAsync(() => _gitService.DiscardHunk(_repoPath, BuildHunkPatch(hunkIndex)),
            confirmDiscard: true, discardWhat: "this hunk");

    [RelayCommand]
    private System.Threading.Tasks.Task StageSelectedLines()
        => ApplyPartialAsync(() => _gitService.StageHunk(_repoPath, BuildSelectedLinesPatch()), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task UnstageSelectedLines()
        => ApplyPartialAsync(() => _gitService.UnstageHunk(_repoPath, BuildSelectedLinesPatch()), confirmDiscard: false);

    [RelayCommand]
    private System.Threading.Tasks.Task DiscardSelectedLines()
        => ApplyPartialAsync(() => _gitService.DiscardHunk(_repoPath, BuildSelectedLinesPatch()),
            confirmDiscard: true, discardWhat: "the selected lines");

    private string BuildHunkPatch(int hunkIndex)
        => _currentPatch == null ? "" : GitLoom.Core.Services.PatchBuilder.BuildHunkPatch(_currentPatch, new[] { hunkIndex });

    // Combines each hunk's selected-line subset under a single file header so the whole
    // selection applies atomically in one `git apply`.
    private string BuildSelectedLinesPatch()
    {
        if (_currentPatch == null) return "";
        var sb = new System.Text.StringBuilder();
        bool any = false;
        foreach (var hunk in Hunks)
        {
            var sel = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(hunk.Lines, l => l.IsSelected), l => l.IndexInHunk));
            if (sel.Count == 0) continue;

            var single = GitLoom.Core.Services.PatchBuilder.BuildLinePatch(_currentPatch, hunk.HunkIndex, sel);
            if (string.IsNullOrEmpty(single)) continue;

            if (!any) { sb.Append(_currentPatch.Header); any = true; }
            sb.Append(single.Substring(_currentPatch.Header.Length)); // drop the duplicate header, keep the hunk body
        }
        return any ? sb.ToString() : "";
    }

    private async System.Threading.Tasks.Task ApplyPartialAsync(System.Action apply, bool confirmDiscard, string? discardWhat = null)
    {
        if (IsBusy || _currentFile == null) return;

        if (confirmDiscard)
        {
            if (!await ConfirmDiscardAsync(discardWhat ?? "these changes")) return;
        }

        IsBusy = true;
        PartialStagingError = null;
        try
        {
            await System.Threading.Tasks.Task.Run(apply);
            // Re-read this file's diff (same direction) so the viewer reflects the new state,
            // and refresh the staging panel counts.
            UpdateDiff(_currentFile);
            _onStagingChanged?.Invoke();
        }
        catch (GitLoom.Core.Exceptions.GitOperationException)
        {
            // Staleness: the file moved under the built patch. Reset the selection and re-parse
            // from a fresh diff — never silently recount / retry.
            UpdateDiff(_currentFile);
            PartialStagingError = "The file changed on disk — selection reset, try again.";
        }
        catch (System.Exception ex)
        {
            PartialStagingError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async System.Threading.Tasks.Task<bool> ConfirmDiscardAsync(string what)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new ConfirmationDialogViewModel
            {
                Title = "Discard Changes",
                Message = $"Discard {what} from the working tree?\nThis cannot be undone.",
                ConfirmButtonText = "Discard"
            };
            var dialog = new Views.ConfirmationDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            return vm.IsConfirmed;
        }
        return false;
    }
}

public partial class DiffHunkRowViewModel : ObservableObject
{
    public int HunkIndex { get; init; }
    public string HeaderText { get; init; } = "";
    public bool IsStaged { get; init; }

    public bool ShowStage => !IsStaged;
    public bool ShowUnstage => IsStaged;
    public bool ShowDiscard => !IsStaged;

    // Unified view: flat line rows (click/drag to select).
    public ObservableCollection<DiffLineRowViewModel> Lines { get; } = new();

    // Side-by-side view: old|new paired rows for this block (block-level accept/discard).
    public ObservableCollection<SideBySideDiffRow> SideRows { get; } = new();
}

public partial class DiffLineRowViewModel : ObservableObject
{
    public int IndexInHunk { get; init; }
    public GitLoom.Core.Models.DiffLineKind Kind { get; init; }
    public string DisplayText { get; init; } = "";

    public bool IsChange => Kind == GitLoom.Core.Models.DiffLineKind.Add || Kind == GitLoom.Core.Models.DiffLineKind.Delete;
    public bool IsAdd => Kind == GitLoom.Core.Models.DiffLineKind.Add;
    public bool IsDelete => Kind == GitLoom.Core.Models.DiffLineKind.Delete;

    // Raised so the parent can recompute HasSelectedLines without holding per-line subscriptions.
    public System.Action? OnSelectionChanged { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => OnSelectionChanged?.Invoke();
}
