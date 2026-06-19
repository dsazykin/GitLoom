using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
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
            return;
        }

        var rawDiff = _gitService.GetFileDiff(_repoPath, file.FilePath, file.IsStaged);

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

                var diffLine = new GitDiffLine { LineType = line[0], Content = line };
                unifiedLines.Add(diffLine);

                char type = line[0];
                if (type == ' ' || type == '@')
                {
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
                    i++;
                }
            }
        }

        DiffLines = unifiedLines;
        SideBySideLines = sbsLines;
    }
}
