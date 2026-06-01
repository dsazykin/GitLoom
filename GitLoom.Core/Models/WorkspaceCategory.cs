using System.Collections.Generic;

namespace GitLoom.Core.Models;

public class WorkspaceCategory
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }

    // Navigation property
    public ICollection<Repository> Repositories { get; set; } = new List<Repository>();
}
