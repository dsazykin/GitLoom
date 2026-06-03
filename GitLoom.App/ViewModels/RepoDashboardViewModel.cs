using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class RepoDashboardViewModel : ViewModelBase
{
    private readonly string _repoPath;
    private readonly IGitService _gitService;
    private readonly RepositoryWatcher _watcher;

    [ObservableProperty]
    private string _repositoryName;

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _stagedFiles = new();

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _unstagedFiles = new();

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _modifiedFiles = new();
    
    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _untrackedFiles = new();
    
    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _deletedFiles = new();
    
    [ObservableProperty]
    private GitFileStatus? _selectedFile;

    [ObservableProperty]
    private ObservableCollection<GitDiffLine> _diffLines = new();
    
    [ObservableProperty]
    private bool _isSideBySideView;

    [ObservableProperty]
    private ObservableCollection<SideBySideDiffRow> _sideBySideLines = new();
    
    [ObservableProperty]
    private int? _aheadCount;

    [ObservableProperty]
    private int? _behindCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = string.Empty;

    partial void OnCommitMessageChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var replaced = value
            .Replace(":smile:", "😄")
            .Replace(":bug:", "🐛")
            .Replace(":sparkles:", "✨")
            .Replace(":memo:", "📝")
            .Replace(":rocket:", "🚀")
            .Replace(":tada:", "🎉")
            .Replace(":white_check_mark:", "✅")
            .Replace(":lipstick:", "💄")
            .Replace(":recycle:", "♻️")
            .Replace(":fire:", "🔥");

        if (replaced != value)
        {
            CommitMessage = replaced;
        }
    }

    // Checks if the user typed a message AND has staged files
    private bool CanCommit => !string.IsNullOrWhiteSpace(CommitMessage) && StagedFiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        _gitService.Commit(_repoPath, CommitMessage);

        // Clear the textbox upon success!
        // (The RepositoryWatcher will automatically notice the new commit and clear the staging lists!)
        CommitMessage = string.Empty;
    }

    // The MVVM toolkit automatically calls this whenever SelectedFile changes
    partial void OnSelectedFileChanged(GitFileStatus? value)
    {
        if (value == null)
        {
            DiffLines.Clear();
            SideBySideLines.Clear();
            return;
        }

        var rawDiff = _gitService.GetFileDiff(_repoPath, value.FilePath, value.IsStaged);

        var unifiedLines = new ObservableCollection<GitDiffLine>();
        var sbsLines = new ObservableCollection<SideBySideDiffRow>();

        if (!string.IsNullOrEmpty(rawDiff))
        {
            var lines = rawDiff.Split('\n');
            int i = 0;

            while (i < lines.Length)
            {
                var line = lines[i];
                if (string.IsNullOrEmpty(line)) { i++; continue; }

                // 1. Build Unified View Line
                var diffLine = new GitDiffLine { LineType = line[0], Content = line };
                unifiedLines.Add(diffLine);

                // 2. Build Side-By-Side Row
                char type = line[0];
                if (type == ' ' || type == '@')
                {
                    // Context or Header lines span both columns
                    sbsLines.Add(new SideBySideDiffRow { LeftLine = diffLine, RightLine = diffLine });
                    i++;
                }
                else if (type == '-' || type == '+')
                {
                    // Gather blocks of consecutive deletions and additions
                    var deletions = new System.Collections.Generic.List<GitDiffLine>();
                    var additions = new System.Collections.Generic.List<GitDiffLine>();

                    while (i < lines.Length && !string.IsNullOrEmpty(lines[i]) && (lines[i][0] == '-' || lines[i][0] == '+'))
                    {
                        var chunkLine = new GitDiffLine { LineType = lines[i][0], Content = lines[i] };
                        unifiedLines.Add(chunkLine); // Also append to unified tracker

                        if (lines[i][0] == '-') deletions.Add(chunkLine);
                        else additions.Add(chunkLine);

                        i++;
                    }

                    // Align the left and right columns
                    int maxRows = System.Math.Max(deletions.Count, additions.Count);
                    var emptyLine = new GitDiffLine { LineType = ' ', Content = "" };

                    for (int j = 0; j < maxRows; j++)
                    {
                        sbsLines.Add(new SideBySideDiffRow {
                            LeftLine = j < deletions.Count ? deletions[j] : emptyLine,
                            RightLine = j < additions.Count ? additions[j] : emptyLine
                        });
                    }
                }
                else
                {
                    i++; // Skip diff file headers (like "diff --git")
                }
            }
        }

        DiffLines = unifiedLines;
        SideBySideLines = sbsLines;
    }

    public RepoDashboardViewModel(Repository repository)
    {
        _repoPath = repository.Path;
        RepositoryName = repository.DisplayName;
        _gitService = new GitService();

        // Load immediately
        RefreshStatus();

        // Start listening for background folder changes
        _watcher = new RepositoryWatcher(_repoPath);
        _watcher.RepositoryChanged += OnRepositoryChanged;
    }

    private void OnRepositoryChanged()
    {
        // The watcher runs on a background thread. UI updates must be dispatched to the main UI thread.
        Dispatcher.UIThread.InvokeAsync(RefreshStatus);
    }

    private void RefreshStatus()
    {
        var allChanges = _gitService.GetRepositoryStatus(_repoPath);

        StagedFiles = new ObservableCollection<GitFileStatus>(allChanges.Where(f => f.IsStaged));
        
        var unstaged = allChanges.Where(f => f.IsUnstaged).ToList();
        UnstagedFiles = new ObservableCollection<GitFileStatus>(unstaged);
        ModifiedFiles = new ObservableCollection<GitFileStatus>(unstaged.Where(f => f.IsModified));
        UntrackedFiles = new ObservableCollection<GitFileStatus>(unstaged.Where(f => f.IsUntracked));
        DeletedFiles = new ObservableCollection<GitFileStatus>(unstaged.Where(f => f.IsDeleted));

        var aheadBehind = _gitService.GetAheadBehind(_repoPath);
        AheadCount = aheadBehind.Ahead;
        BehindCount = aheadBehind.Behind;

        CommitCommand.NotifyCanExecuteChanged();
    }
    
    [RelayCommand]
    private void StageFile(GitFileStatus file)
    {
        _gitService.StageFile(_repoPath, file.FilePath);

        // Note: We don't need to manually refresh the lists here!
        // Modifying the index will automatically trigger our RepositoryWatcher,
        // which instantly re-runs RefreshStatus() and updates the UI!
    }

    [RelayCommand]
    private void UnstageFile(GitFileStatus file)
    {
        _gitService.UnstageFile(_repoPath, file.FilePath);
    }
    
    [RelayCommand]
    private void StageSelectedFiles(IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var paths = selectedItems.Cast<GitFileStatus>().Select(f => f.FilePath).ToList();
        _gitService.StageFiles(_repoPath, paths);
    }

    [RelayCommand]
    private void UnstageSelectedFiles(IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var paths = selectedItems.Cast<GitFileStatus>().Select(f => f.FilePath).ToList();
        _gitService.UnstageFiles(_repoPath, paths);
    }
    
    [RelayCommand]
    private void StageAllFiles()
    {
        if (UnstagedFiles.Count == 0) return;

        // Grab every single file path in the unstaged list and stage them all!
        var paths = UnstagedFiles.Select(f => f.FilePath).ToList();
        _gitService.StageFiles(_repoPath, paths);
    }

    [RelayCommand]
    private void UnstageAllFiles()
    {
        if (StagedFiles.Count == 0) return;

        // Grab every single file path in the staged list and unstage them all!
        var paths = StagedFiles.Select(f => f.FilePath).ToList();
        _gitService.UnstageFiles(_repoPath, paths);
    }
    
    [RelayCommand]
    private void Push()
    {
        try
        {
            _gitService.Push(_repoPath);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Push Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Pull()
    {
        try
        {
            _gitService.Pull(_repoPath);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Pull Failed: {ex.Message}");
        }
    }
}