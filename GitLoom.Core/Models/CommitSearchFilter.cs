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
}
