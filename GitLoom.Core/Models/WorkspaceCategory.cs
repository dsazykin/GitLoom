using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GitLoom.Core.Models;

public class WorkspaceCategory
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }

    // Navigation property
    public ObservableCollection<Repository> Repositories { get; set; } = new ObservableCollection<Repository>();
}
