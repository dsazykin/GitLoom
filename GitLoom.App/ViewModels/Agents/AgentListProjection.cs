using System.Collections.Generic;
using System.Linq;
using GitLoom.Core.Agents;

namespace GitLoom.App.ViewModels.Agents;

/// <summary>
/// Pure LIFO projection for the section-rail agent list (P2-13 Row 1): newest agent first,
/// ordered by spawn time. Extracted so both the live rail (<c>ControlCenterViewModel</c>) and
/// <c>ActivityBarOrderingTests</c> exercise the exact same ordering — the ordering is not
/// re-implemented in the test.
/// </summary>
public static class AgentListProjection
{
    /// <summary>Newest-spawned first (LIFO). A stable, total ordering, so removing any element
    /// leaves the relative order of the rest unchanged.</summary>
    public static IReadOnlyList<AgentInfo> LifoOrder(IEnumerable<AgentInfo> agents) =>
        agents.OrderByDescending(a => a.SpawnedAt).ToList();
}
