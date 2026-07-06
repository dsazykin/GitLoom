using System;
using System.Collections.Generic;

namespace GitLoom.Core.Models;

public class CommitSearchFilter
{
    public string? Text { get; set; }
    public string? BranchName { get; set; }
    public string? Author { get; set; }
    public List<string>? FilePaths { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }

    /// <summary>
    /// When true, restrict the walk to commits reachable from HEAD and its upstream (T-09
    /// "current branch only"). Ignored when an explicit <see cref="BranchName"/> is set.
    /// </summary>
    public bool CurrentBranchOnly { get; set; }
}
