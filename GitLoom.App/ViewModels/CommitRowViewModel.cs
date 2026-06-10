using GitLoom.Core.Models;
using GitLoom.Core.Graph;

namespace GitLoom.App.ViewModels;

public class CommitRowViewModel
{
    public GitCommitItem Commit { get; set; } = null!;
    public GraphNode Node { get; set; } = null!;
}