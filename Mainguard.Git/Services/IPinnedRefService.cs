using System.Collections.Generic;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Persists the branches/tags a user has pinned in a repository's commit graph (T-09).
/// Pinned refs are returned in pin order and feed the router so they take the left-most lanes.
/// </summary>
public interface IPinnedRefService
{
    /// <summary>Pinned refs for the repo, ordered by <see cref="PinnedRef.Order"/> ascending.</summary>
    IReadOnlyList<PinnedRef> GetPinnedRefs(string repoPath);

    /// <summary>Pins a ref (appended after existing pins). No-op if already pinned.</summary>
    void Pin(string repoPath, string refName);

    /// <summary>Removes a pin. No-op if the ref was not pinned.</summary>
    void Unpin(string repoPath, string refName);

    bool IsPinned(string repoPath, string refName);
}
