namespace Mainguard.Git.Models;

/// <summary>
/// A branch/tag the user has pinned in a repository's commit graph (T-09). Pinned refs are
/// ordered first into the <see cref="Graph.CommitGraphRouter"/> input (by <see cref="Order"/>,
/// ascending), so they take the left-most lanes. Persisted per repo via <see cref="AppDbContext"/>
/// so pins survive restarts.
/// </summary>
public sealed class PinnedRef
{
    public int Id { get; set; }

    /// <summary>Absolute path of the repository this pin belongs to.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>The ref name (branch friendly name or tag name) that is pinned.</summary>
    public string RefName { get; set; } = "";

    /// <summary>Pin priority — lower values sort first (left-most lane).</summary>
    public int Order { get; set; }
}
