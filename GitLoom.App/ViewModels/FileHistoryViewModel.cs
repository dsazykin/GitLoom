using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Drives the dedicated file-history view (T-12): a newest-first, rename-following list of the
/// revisions that touched one file (left) and the diff of the selected revision against its
/// predecessor (right). History loads off the UI thread; the per-revision diff is recomputed on
/// selection, also off the UI thread and with cancellation, so rapid arrow-key paging never renders
/// a stale diff. The optional line-history filter narrows the list to revisions whose diff touches a
/// chosen line range, reusing the T-06 <see cref="PatchParser"/> via <see cref="LineHistoryFilter"/>
/// (documented there as an approximation of <c>git log -L</c>).
/// </summary>
public partial class FileHistoryViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;

    // The full, unfiltered newest-first history. Versions (the bound list) is either this or the
    // line-filtered subset; predecessors for diffing are always resolved against this full list so a
    // filtered view still diffs against the true previous revision.
    private IReadOnlyList<FileVersion> _allVersions = Array.Empty<FileVersion>();
    private readonly Dictionary<string, int> _indexBySha = new();

    // Cancels the in-flight diff load when the selection changes again before it completes.
    private CancellationTokenSource? _diffCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private ObservableCollection<FileVersion> _versions = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private FileVersion? _selectedVersion;

    [ObservableProperty]
    private ObservableCollection<GitDiffLine> _diffLines = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDiffLines))]
    private string? _diffPlaceholder;

    [ObservableProperty]
    private string _versionCountText = string.Empty;

    // ---- Line-history filter (v1, approximates `git log -L`) ----------------

    [ObservableProperty]
    private string _lineRangeStart = string.Empty;

    [ObservableProperty]
    private string _lineRangeEnd = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLineFilterActive))]
    private string? _lineFilterSummary;

    public bool HasFile => !string.IsNullOrEmpty(FilePath);
    public bool HasSelection => SelectedVersion != null;
    public bool HasDiffLines => string.IsNullOrEmpty(DiffPlaceholder);
    public bool IsLineFilterActive => !string.IsNullOrEmpty(LineFilterSummary);

    public FileHistoryViewModel(IGitService gitService, string repoPath, string filePath)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        // libgit2 reports paths with forward slashes; normalize so the log query matches.
        FilePath = (filePath ?? string.Empty).Replace('\\', '/');
    }

    /// <summary>Loads the file's history off the UI thread and selects the newest revision.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var path = FilePath;
            var history = await Task.Run(() => _gitService.GetFileHistory(_repoPath, path));
            _allVersions = history;

            _indexBySha.Clear();
            for (int i = 0; i < history.Count; i++) _indexBySha[history[i].Sha] = i;

            ClearLineFilterState();
            Versions = new ObservableCollection<FileVersion>(history);
            UpdateVersionCount();

            SelectedVersion = Versions.FirstOrDefault();
            if (SelectedVersion == null)
            {
                // No revisions touched the path (e.g. an untracked file) — nothing to diff.
                DiffLines = new ObservableCollection<GitDiffLine>();
                DiffPlaceholder = "No committed history for this file.";
            }
        }
        catch (GitLoomException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSelectedVersionChanged(FileVersion? value)
    {
        // Fire-and-forget: the load cancels any prior in-flight diff and marshals its own result.
        _ = LoadDiffAsync(value);
    }

    private async Task LoadDiffAsync(FileVersion? version)
    {
        _diffCts?.Cancel();

        if (version == null)
        {
            DiffLines = new ObservableCollection<GitDiffLine>();
            DiffPlaceholder = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _diffCts = cts;
        var token = cts.Token;

        try
        {
            var rendered = await Task.Run(() => BuildDiffRows(version), token);
            if (token.IsCancellationRequested) return;   // a newer selection superseded us

            DiffPlaceholder = rendered.Placeholder;
            DiffLines = new ObservableCollection<GitDiffLine>(rendered.Lines);
        }
        catch (OperationCanceledException)
        {
            // Superseded before it ran — nothing to render.
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            DiffLines = new ObservableCollection<GitDiffLine>();
            DiffPlaceholder = ex.Message;
        }
    }

    // Computes the diff of a revision against its predecessor (the previous revision that touched the
    // file) and renders it into flat unified rows via PatchParser. The oldest revision has no
    // predecessor, so its "diff" is the file's introduction — every line as an addition.
    private (IReadOnlyList<GitDiffLine> Lines, string? Placeholder) BuildDiffRows(FileVersion version)
    {
        string patch;
        try
        {
            patch = PatchForVersion(version) ?? string.Empty;
        }
        catch (GitOperationException ex)
        {
            // Binary blob at the introducing revision (GetFileAtCommit throws typed): placeholder,
            // never garbage.
            return (Array.Empty<GitDiffLine>(), ex.Message);
        }

        if (string.IsNullOrWhiteSpace(patch))
        {
            return (Array.Empty<GitDiffLine>(), "No textual changes in this revision.");
        }

        if (LooksBinary(patch))
        {
            return (Array.Empty<GitDiffLine>(), "Binary file — no textual diff to display.");
        }

        var rows = RenderUnified(patch);
        return rows.Count == 0
            ? (Array.Empty<GitDiffLine>(), "No textual changes in this revision.")
            : (rows, null);
    }

    // The unified diff of a version against its predecessor. For the oldest revision (no predecessor)
    // the file was introduced here, so synthesize an all-additions patch from the blob text.
    private string? PatchForVersion(FileVersion version)
    {
        if (!_indexBySha.TryGetValue(version.Sha, out var index)) return null;

        int predecessorIndex = index + 1;   // list is newest-first, so the predecessor is one older
        if (predecessorIndex < _allVersions.Count)
        {
            var predecessor = _allVersions[predecessorIndex];
            return _gitService.GetFileDiffBetweenCommits(
                _repoPath, predecessor.Sha, version.Sha, version.PathAtCommit);
        }

        // Introducing revision — throws typed if the introduced blob is binary.
        var content = _gitService.GetFileAtCommit(_repoPath, version.Sha, version.PathAtCommit);
        return BuildIntroductionPatch(content);
    }

    private static bool LooksBinary(string patch) =>
        patch.Contains("Binary files ", StringComparison.Ordinal) ||
        patch.Contains("GIT binary patch", StringComparison.Ordinal);

    // Renders a unified diff into flat rows: one header row per hunk, then its lines with the
    // +/-/space prefix preserved so the view's IsAdded/IsRemoved/IsHeader styles light up.
    private static IReadOnlyList<GitDiffLine> RenderUnified(string patch)
    {
        var rows = new List<GitDiffLine>();
        foreach (var file in PatchParser.Parse(patch))
        {
            foreach (var hunk in file.Hunks)
            {
                rows.Add(new GitDiffLine { LineType = '@', Content = HunkHeaderText(hunk) });
                foreach (var line in hunk.Lines)
                {
                    char prefix = line.Kind switch
                    {
                        DiffLineKind.Add => '+',
                        DiffLineKind.Delete => '-',
                        _ => ' '
                    };
                    rows.Add(new GitDiffLine { LineType = prefix, Content = prefix + line.Text });
                }
            }
        }
        return rows;
    }

    private static string HunkHeaderText(DiffHunk h)
    {
        var oldSpan = h.OldCountOmitted ? $"{h.OldStart}" : $"{h.OldStart},{h.OldCount}";
        var newSpan = h.NewCountOmitted ? $"{h.NewStart}" : $"{h.NewStart},{h.NewCount}";
        return $"@@ -{oldSpan} +{newSpan} @@{h.SectionHeading}";
    }

    // Synthesizes an all-additions unified patch for a file's introduction, so the introducing
    // revision renders (and line-filters) uniformly with every other revision.
    private static string BuildIntroductionPatch(string content)
    {
        if (string.IsNullOrEmpty(content)) return string.Empty;

        var lines = content.Replace("\r\n", "\n").Split('\n');
        // A trailing newline yields an empty final element; drop it so counts match git.
        int count = lines.Length;
        if (count > 0 && lines[count - 1].Length == 0) count--;
        if (count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.Append("@@ -0,0 +1,").Append(count).Append(" @@\n");
        for (int i = 0; i < count; i++) sb.Append('+').Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    // ---- Line-history filter commands --------------------------------------

    [RelayCommand]
    private async Task ApplyLineFilter()
    {
        if (!TryParseRange(out int start, out int end))
        {
            LineFilterSummary = null;
            ErrorMessage = "Enter a valid line range (e.g. 10 and 20).";
            return;
        }

        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var full = _allVersions;
            var kept = await Task.Run(() =>
                LineHistoryFilter.FilterByLineRange(full, start, end, v =>
                {
                    try { return PatchForVersion(v); }
                    catch (GitOperationException) { return null; }   // binary introduction — can't intersect textually
                }));

            Versions = new ObservableCollection<FileVersion>(kept);
            LineFilterSummary =
                $"Lines {start}–{end}: {kept.Count} of {full.Count} revisions (approximates git log -L).";
            UpdateVersionCount();

            SelectedVersion = Versions.FirstOrDefault();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ClearLineFilter()
    {
        ClearLineFilterState();
        Versions = new ObservableCollection<FileVersion>(_allVersions);
        UpdateVersionCount();
        SelectedVersion = Versions.FirstOrDefault();
    }

    private void ClearLineFilterState()
    {
        LineFilterSummary = null;
        LineRangeStart = string.Empty;
        LineRangeEnd = string.Empty;
    }

    private bool TryParseRange(out int start, out int end)
    {
        start = end = 0;
        if (!int.TryParse(LineRangeStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 1)
            return false;

        // An empty end defaults to a single-line range.
        if (string.IsNullOrWhiteSpace(LineRangeEnd)) { end = start; return true; }

        if (!int.TryParse(LineRangeEnd, NumberStyles.Integer, CultureInfo.InvariantCulture, out end) || end < 1)
            return false;
        return true;
    }

    private void UpdateVersionCount()
    {
        int n = Versions.Count;
        VersionCountText = n == 1 ? "1 revision" : $"{n} revisions";
    }
}
