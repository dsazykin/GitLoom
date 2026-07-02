using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Graph;

public class CommitGraphRouter
{
    public GraphRouteResult RouteCommits(IEnumerable<GitCommitItem> commits, GraphFringeState incomingFringe)
    {
        var result = new GraphRouteResult();

        // Clone the incoming active lanes to mutate them as we walk down the graph
        var activeLanes = new List<string>(incomingFringe.ActiveLanes);
        int rowIndex = 0;

        foreach (var commit in commits)
        {
            var incomingLanes = new List<string>(activeLanes);

            var node = new GraphNode
            {
                CommitSha = commit.Sha,
                ParentShas = new List<string>(commit.ParentShas),
                RowIndex = rowIndex++
            };

            // Find the lane this commit belongs to
            int laneIndex = activeLanes.IndexOf(commit.Sha);

            // If it's not in any active lane (e.g. it's the very first commit or a newly checked out branch tip)
            if (laneIndex == -1)
            {
                laneIndex = activeLanes.FindIndex(string.IsNullOrEmpty);
                if (laneIndex == -1)
                {
                    laneIndex = activeLanes.Count;
                    activeLanes.Add(commit.Sha);
                }
                else
                {
                    activeLanes[laneIndex] = commit.Sha;
                }
            }

            node.LaneIndex = laneIndex;

            // Consume this commit from the active lane, and replace it with its FIRST parent (straight line down)
            if (commit.ParentShas.Count > 0)
            {
                string firstParent = commit.ParentShas[0];
                int existingParentLane = activeLanes.IndexOf(firstParent);

                if (existingParentLane != -1 && existingParentLane != laneIndex)
                {
                    // CONFLICT! Two branches are fighting for the same parent.
                    // We ALWAYS enforce left-most lane dominance to keep the main trunk perfectly straight.
                    if (laneIndex < existingParentLane)
                    {
                        // Pull the parent into THIS lane (the more important, left-most lane)
                        activeLanes[laneIndex] = firstParent;
                        activeLanes[existingParentLane] = string.Empty; // Force the right branch to close
                    }
                    else
                    {
                        // Close this branch and let the parent continue straight down the dominant left lane
                        activeLanes[laneIndex] = string.Empty;
                    }
                }
                else
                {
                    activeLanes[laneIndex] = firstParent;
                }
            }
            else
            {
                activeLanes[laneIndex] = string.Empty; // Initial commit reached, end of branch line
            }

            // If there are additional parents (a merge commit!), allocate new parallel lanes for them
            for (int i = 1; i < commit.ParentShas.Count; i++)
            {
                string parentSha = commit.ParentShas[i];
                if (!activeLanes.Contains(parentSha))
                {
                    int emptySlot = activeLanes.FindIndex(string.IsNullOrEmpty);
                    if (emptySlot == -1)
                    {
                        activeLanes.Add(parentSha);
                    }
                    else
                    {
                        activeLanes[emptySlot] = parentSha;
                    }
                }
            }

            // Record Incoming top-half lines
            for (int i = 0; i < incomingLanes.Count; i++)
            {
                if (!string.IsNullOrEmpty(incomingLanes[i]))
                    node.IncomingLanes.Add(i);
            }

            // Calculate Outgoing bottom-half lines
            // Draw lines from the active commit dot down to all of its parents
            foreach (var parentSha in commit.ParentShas)
            {
                int parentLane = activeLanes.IndexOf(parentSha);
                if (parentLane != -1)
                {
                    node.OutgoingLines.Add(new GraphLine { FromLane = node.LaneIndex, ToLane = parentLane });
                }
            }

            // Draw passthrough lines for all OTHER parallel branches
            for (int i = 0; i < incomingLanes.Count; i++)
            {
                // Skip the active commit lane, we just handled it above!
                if (i == node.LaneIndex) continue;

                // Skip empty lanes
                if (string.IsNullOrEmpty(incomingLanes[i])) continue;

                // Trace where this parallel branch ended up in the active lanes array
                int newLane = activeLanes.IndexOf(incomingLanes[i]);
                if (newLane != -1)
                {
                    node.OutgoingLines.Add(new GraphLine { FromLane = i, ToLane = newLane });
                }
            }

            result.Nodes.Add(node);
        }

        // Output the new "Fringe" state so the next chunk of 50 commits knows where the branch lines left off
        result.EndFringe.ActiveLanes = activeLanes;

        return result;
    }
}
