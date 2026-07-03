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
            var absolutePath = System.IO.Path.Combine(_repoPath, FilePath);
            var vm = new ConflictResolverWindowViewModel(absolutePath, new Avalonia.Controls.Window()); // Will set window in codebehind
            var dialog = new Views.ConflictResolverWindow { DataContext = vm };
            vm.GetType().GetField("_window", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(vm, dialog);

            var result = await dialog.ShowDialog<bool>(desktop.MainWindow);
            if (result)
            {
                // Auto-stage the file since it was marked as resolved
                _gitService.StageFile(_repoPath, FilePath);

                // Refresh the UI to show the new state
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

    public DiffViewerViewModel(IGitService gitService, string repoPath)
    {
        _gitService = gitService;
        _repoPath = repoPath;
    }

    public void UpdateDiff(GitFileStatus? file)
    {
        if (file == null)
        {
            DiffLines.Clear();
            SideBySideLines.Clear();
            RawContent = string.Empty;
            FilePath = string.Empty;
            return;
        }

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
    }
}
