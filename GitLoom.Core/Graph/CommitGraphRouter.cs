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
            var node = new GraphNode
            {
                CommitSha = commit.Sha,
                ParentShas = new List<string>(commit.ParentShas),
                RowIndex = rowIndex++
            };

            // 1. Find the lane this commit belongs to
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

            // 2. Consume this commit from the active lane, and replace it with its FIRST parent (straight line down)
            if (commit.ParentShas.Count > 0)
            {
                activeLanes[laneIndex] = commit.ParentShas[0];
            }
            else
            {
                activeLanes[laneIndex] = string.Empty; // Initial commit reached, end of branch line
            }

            // 3. If there are additional parents (a merge commit!), allocate new parallel lanes for them
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

            result.Nodes.Add(node);
        }

        // Output the new "Fringe" state so the next chunk of 50 commits knows where the branch lines left off
        result.EndFringe.ActiveLanes = activeLanes;

        return result;
    }
}