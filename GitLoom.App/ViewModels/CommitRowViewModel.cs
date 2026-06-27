using GitLoom.Core.Models;
using GitLoom.Core.Graph;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitLoom.App.ViewModels;

public partial class CommitRowViewModel : ObservableObject
{
    public GitCommitItem Commit { get; set; } = null!;
    public GraphNode Node { get; set; } = null!;

    [ObservableProperty]
    private bool _isHighlighted;
}