using System;

namespace GitLoom.Core.Models;

public class Repository
{
    public int RepositoryId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    public string CustomIconColor { get; set; } = string.Empty;

    public int CategoryId { get; set; }
    
    // Navigation property
    public WorkspaceCategory? Category { get; set; }
}
