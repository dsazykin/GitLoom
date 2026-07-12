using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Graph;

/// <summary>
/// Assigns commits to graph lanes and routes the connecting lines (Hotspot Register H2).
///
/// Layout policy (this <em>is</em> the crossing-minimization strategy, so it is documented here):
/// <list type="bullet">
/// <item><b>Left-most lane dominance</b> — when two lanes converge on the same first parent, the
/// left (more important) lane always wins and the right lane closes, which keeps the main trunk a
/// straight line and makes every branch line rejoin leftwards exactly once (no braiding).</item>
/// <item><b>Left-most free-slot allocation</b> — a new branch line always opens in the left-most
/// empty lane, so the graph stays as narrow as the DAG allows and lines never cross idle lanes.</item>
/// <item><b>Pinned refs first</b> (T-09) — pinned tips pre-reserve the left-most lanes.</item>
/// </list>
///
/// Complexity: one commit costs O(active-lanes) — the passthrough lines alone are Θ(L) of output —
/// and every SHA lookup is O(1) via a lane-index dictionary kept in lock-step with the lane list
/// (the pre-optimization linear <c>IndexOf</c>/<c>Contains</c>/<c>FindIndex</c> scans made a wide
/// DAG cost O(commits × L²); see <c>CommitGraphRouterWideDagTests</c> for the measured budget net).
/// The router never holds a repository handle — pure input → output.
/// </summary>
public class CommitGraphRouter
{
    public GraphRouteResult RouteCommits(IEnumerable<GitCommitItem> commits, GraphFringeState incomingFringe)
        => RouteCommits(commits, incomingFringe, null);

    /// <summary>
    /// Routes a chunk of commits into graph lanes. <paramref name="priorityTips"/> (T-09 pinned
    /// refs, in pin order) reserve the left-most lanes for the first chunk: their tip SHAs are
    /// pre-seeded into the lane array so a pinned ref claims lane 0/1/… even if a non-pinned tip
    /// appears earlier in the topo walk. A reserved lane draws nothing until its tip commit is
    /// actually reached (no dangling stubs), and seeding is skipped entirely when nothing is
    /// pinned, so the un-pinned graph is byte-for-byte unchanged.
    /// </summary>
    public GraphRouteResult RouteCommits(IEnumerable<GitCommitItem> commits, GraphFringeState incomingFringe,
        IReadOnlyList<string>? priorityTips)
    {
        var result = new GraphRouteResult();

        // Clone the incoming active lanes to mutate them as we walk down the graph. The dictionary
        // mirrors the list (SHA → lane index) for O(1) lookups, and the sorted set holds the empty
        // slots so "left-most free lane" is O(log L) instead of a linear scan. Router-produced
        // fringes never hold the same SHA in two lanes (first-parent conflicts collapse to one lane,
        // merge parents are only seated when absent), so the mirror is a bijection; if a hand-built
        // fringe ever carried a duplicate, first-occurrence wins — matching List.IndexOf.
        var activeLanes = new List<string>(incomingFringe.ActiveLanes);
        var laneBySha = new Dictionary<string, int>(activeLanes.Count);
        var freeLanes = new SortedSet<int>();
        for (int i = 0; i < activeLanes.Count; i++)
        {
            if (string.IsNullOrEmpty(activeLanes[i])) freeLanes.Add(i);
            else laneBySha.TryAdd(activeLanes[i], i);
        }

        int LaneOf(string sha) => laneBySha.TryGetValue(sha, out var idx) ? idx : -1;

        // Seats a SHA in a lane, keeping list/dict/free-set in lock-step.
        void SetLane(int index, string sha)
        {
            var old = activeLanes[index];
            if (!string.IsNullOrEmpty(old) && laneBySha.TryGetValue(old, out var oi) && oi == index)
                laneBySha.Remove(old);
            activeLanes[index] = sha;
            laneBySha[sha] = index;
            freeLanes.Remove(index);
        }

        void ClearLane(int index)
        {
            var old = activeLanes[index];
            if (!string.IsNullOrEmpty(old) && laneBySha.TryGetValue(old, out var oi) && oi == index)
                laneBySha.Remove(old);
            activeLanes[index] = string.Empty;
            freeLanes.Add(index);
        }

        // Left-most empty slot, or a fresh lane appended on the right.
        int TakeFreeLane(string sha)
        {
            if (freeLanes.Count > 0)
            {
                int idx = freeLanes.Min;
                SetLane(idx, sha);
                return idx;
            }
            activeLanes.Add(sha);
            laneBySha[sha] = activeLanes.Count - 1;
            return activeLanes.Count - 1;
        }

        int rowIndex = 0;

        // Reserve left-most lanes for pinned refs on the first chunk (fringe empty). Reserved lanes
        // stay "pending" — invisible — until their tip commit is encountered and realized.
        var pendingSeeds = new HashSet<string>();
        if (activeLanes.Count == 0 && priorityTips != null)
        {
            foreach (var tip in priorityTips)
            {
                if (!string.IsNullOrEmpty(tip) && !laneBySha.ContainsKey(tip))
                {
                    activeLanes.Add(tip);
                    laneBySha[tip] = activeLanes.Count - 1;
                    pendingSeeds.Add(tip);
                }
            }
        }

        foreach (var commit in commits)
        {
            var incomingLanes = new List<string>(activeLanes);

            var node = new GraphNode
            {
                CommitSha = commit.Sha,
                ParentShas = new List<string>(commit.ParentShas),
                RowIndex = rowIndex++,
                // Pre-sized: a wide row records ~one incoming lane and ~one outgoing line per
                // active lane — letting these grow from 0 re-copies each list ~log(L) times per row.
                IncomingLanes = new List<int>(incomingLanes.Count),
                OutgoingLines = new List<GraphLine>(incomingLanes.Count + commit.ParentShas.Count)
            };

            // Find the lane this commit belongs to; if it's not in any active lane (e.g. it's the
            // very first commit or a newly checked out branch tip), open the left-most free one.
            int laneIndex = LaneOf(commit.Sha);
            if (laneIndex == -1) laneIndex = TakeFreeLane(commit.Sha);

            node.LaneIndex = laneIndex;

            // Consume this commit from the active lane, and replace it with its FIRST parent (straight line down)
            if (commit.ParentShas.Count > 0)
            {
                string firstParent = commit.ParentShas[0];
                int existingParentLane = LaneOf(firstParent);

                if (existingParentLane != -1 && existingParentLane != laneIndex)
                {
                    // CONFLICT! Two branches are fighting for the same parent.
                    // We ALWAYS enforce left-most lane dominance to keep the main trunk perfectly straight.
                    if (laneIndex < existingParentLane)
                    {
                        // Pull the parent into THIS lane (the more important, left-most lane)
                        SetLane(laneIndex, firstParent);
                        ClearLane(existingParentLane); // Force the right branch to close
                    }
                    else
                    {
                        // Close this branch and let the parent continue straight down the dominant left lane
                        ClearLane(laneIndex);
                    }
                }
                else
                {
                    SetLane(laneIndex, firstParent);
                }
            }
            else
            {
                ClearLane(laneIndex); // Initial commit reached, end of branch line
            }

            // If there are additional parents (a merge commit!), allocate new parallel lanes for them
            for (int i = 1; i < commit.ParentShas.Count; i++)
            {
                string parentSha = commit.ParentShas[i];
                if (!laneBySha.ContainsKey(parentSha))
                {
                    TakeFreeLane(parentSha);
                }
            }

            // Record Incoming top-half lines (a reserved-but-unrealized pinned lane draws nothing)
            for (int i = 0; i < incomingLanes.Count; i++)
            {
                if (!string.IsNullOrEmpty(incomingLanes[i]) && !pendingSeeds.Contains(incomingLanes[i]))
                    node.IncomingLanes.Add(i);
            }

            // Calculate Outgoing bottom-half lines
            // Draw lines from the active commit dot down to all of its parents
            foreach (var parentSha in commit.ParentShas)
            {
                int parentLane = LaneOf(parentSha);
                if (parentLane != -1)
                {
                    node.OutgoingLines.Add(new GraphLine(node.LaneIndex, parentLane));
                }
            }

            // Draw passthrough lines for all OTHER parallel branches
            for (int i = 0; i < incomingLanes.Count; i++)
            {
                // Skip the active commit lane, we just handled it above!
                if (i == node.LaneIndex) continue;

                // Skip empty lanes and reserved-but-unrealized pinned lanes
                if (string.IsNullOrEmpty(incomingLanes[i]) || pendingSeeds.Contains(incomingLanes[i])) continue;

                // Trace where this parallel branch ended up in the active lanes array
                int newLane = LaneOf(incomingLanes[i]);
                if (newLane != -1)
                {
                    node.OutgoingLines.Add(new GraphLine(i, newLane));
                }
            }

            result.Nodes.Add(node);
        }

        // Output the new "Fringe" state so the next chunk of 50 commits knows where the branch lines left off
        result.EndFringe.ActiveLanes = activeLanes;

        return result;
    }
}
