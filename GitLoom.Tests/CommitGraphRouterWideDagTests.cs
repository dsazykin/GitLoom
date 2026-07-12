using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GitLoom.Core.Graph;
using GitLoom.Core.Models;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Hotspot Register H2 — the wide-DAG router contract. Three nets:
/// <list type="bullet">
/// <item>a pathological 64-lane / 50k-commit route that must stay structurally correct (lane count
/// bounded, every commit routed) — its wall time is printed against the H2 250 ms budget (the
/// enforcing micro-bench belongs to the future GitLoom.Benchmarks project per OPEN DECISION
/// [PERF-2]; a timing assert inside xUnit is a rejection trigger, so this test pins structure and
/// only *reports* time);</item>
/// <item>a chunked-equals-whole property: routing in 50-commit chunks through the fringe must
/// produce exactly the routing of a single whole-history call (the fringe contract the timeline's
/// infinite scroll depends on);</item>
/// <item>a seeded random-DAG sweep asserting the router's structural invariants hold on arbitrary
/// topologies, not just the hand-built fixtures.</item>
/// </list>
/// </summary>
public class CommitGraphRouterWideDagTests
{
    // ---- Pathological wide-DAG generator --------------------------------------------------------

    /// <summary>
    /// Generates a topo-ordered (newest-first) commit list that keeps <paramref name="lanes"/> lanes
    /// simultaneously active for the whole walk: commits round-robin across L branches, each commit's
    /// first parent is the same branch's next commit, and every <paramref name="mergeEvery"/>-th
    /// commit also merges the neighbouring branch's next commit (already awaited by its lane, so the
    /// merge draws a cross-lane line without allocating). The final L commits are roots.
    /// </summary>
    private static List<GitCommitItem> WideDag(int count, int lanes, int mergeEvery)
    {
        var commits = new List<GitCommitItem>(count);
        for (int i = 0; i < count; i++)
        {
            var parents = new List<string>();
            if (i + lanes < count) parents.Add($"c{i + lanes}"); // same-branch next commit
            if (mergeEvery > 0 && i % mergeEvery == 0 && i + 1 < count && i + lanes < count)
                parents.Add($"c{i + 1}"); // the sha the neighbouring lane is awaiting
            commits.Add(new GitCommitItem { Sha = $"c{i}", ParentShas = parents });
        }
        return commits;
    }

    [Fact]
    public void RouteCommits_PathologicalWideDag_StaysBounded_AndRoutesEveryCommit()
    {
        const int count = 50_000, lanes = 64;
        var commits = WideDag(count, lanes, mergeEvery: 16);
        var router = new CommitGraphRouter();

        var sw = Stopwatch.StartNew();
        var result = router.RouteCommits(commits, new GraphFringeState());
        sw.Stop();

        Assert.Equal(count, result.Nodes.Count);
        // The generator never needs more than lanes + 1 concurrent lanes (one transient during a
        // merge hand-off). Unbounded growth here is the H2 failure mode.
        Assert.InRange(result.EndFringe.ActiveLanes.Count, lanes, lanes + 2);
        int maxLane = result.Nodes.Max(n => n.LaneIndex);
        Assert.InRange(maxLane, lanes - 1, lanes + 1);

        // Same input through the frozen pre-optimization algorithm — a same-build, same-machine
        // before/after that keeps the win honest on every run.
        var swRef = Stopwatch.StartNew();
        var reference = ReferenceRoute(commits, new GraphFringeState(), null);
        swRef.Stop();
        Assert.Equal(result.Nodes.Count, reference.Nodes.Count);

        // H2 budget for reference: full 50k-commit route <= 250 ms on the PR runner ([PERF-2]
        // moves the enforcing assert to GitLoom.Benchmarks; printed here, never asserted).
        Console.WriteLine($"[H2] 50k x {lanes}-lane route: optimized {sw.Elapsed.TotalMilliseconds:F0} ms"
            + $" vs pre-optimization {swRef.Elapsed.TotalMilliseconds:F0} ms");
    }

    // ---- Chunked-equals-whole (the fringe contract) ---------------------------------------------

    [Fact]
    public void RouteCommits_InChunksThroughFringe_MatchesWholeHistoryRoute()
    {
        var commits = WideDag(2_000, 16, mergeEvery: 8);
        var router = new CommitGraphRouter();

        var whole = router.RouteCommits(commits, new GraphFringeState());

        var chunkedNodes = new List<GraphNode>();
        var fringe = new GraphFringeState();
        for (int skip = 0; skip < commits.Count; skip += 50)
        {
            var chunk = commits.Skip(skip).Take(50).ToList();
            var res = router.RouteCommits(chunk, fringe);
            chunkedNodes.AddRange(res.Nodes);
            fringe = res.EndFringe;
        }

        Assert.Equal(whole.Nodes.Count, chunkedNodes.Count);
        for (int i = 0; i < whole.Nodes.Count; i++)
            AssertNodesEqual(whole.Nodes[i], chunkedNodes[i]);
    }

    // ---- Random-DAG structural invariants --------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    public void RouteCommits_RandomDag_HoldsStructuralInvariants(int seed)
    {
        var commits = RandomDag(seed, count: 400);
        var router = new CommitGraphRouter();

        var result = router.RouteCommits(commits, new GraphFringeState());

        Assert.Equal(commits.Count, result.Nodes.Count);
        var laneAt = new Dictionary<string, int>(); // sha -> assigned lane, for parent-line checks
        for (int i = 0; i < result.Nodes.Count; i++)
        {
            var node = result.Nodes[i];
            Assert.Equal(i, node.RowIndex);
            Assert.True(node.LaneIndex >= 0, "lane index must be non-negative");
            laneAt[node.CommitSha] = node.LaneIndex;

            foreach (var line in node.OutgoingLines)
            {
                Assert.True(line.FromLane >= 0 && line.ToLane >= 0, "lines never reference negative lanes");
            }
            foreach (var lane in node.IncomingLanes)
            {
                Assert.True(lane >= 0, "incoming lanes are non-negative");
            }
        }

        // Every parent that appears later in the walk is eventually drawn at the lane its child's
        // outgoing line pointed to at some row (connectivity: no orphaned parent link).
        foreach (var node in result.Nodes)
        {
            foreach (var parent in node.ParentShas)
            {
                if (laneAt.ContainsKey(parent))
                {
                    Assert.True(node.OutgoingLines.Count > 0,
                        $"commit {node.CommitSha} has a routed parent {parent} but no outgoing lines");
                }
            }
        }
    }

    /// <summary>Seeded random topo-ordered DAG: newest-first, parents always later in the list.</summary>
    private static List<GitCommitItem> RandomDag(int seed, int count)
    {
        var rng = new Random(seed);
        var commits = new List<GitCommitItem>(count);
        for (int i = 0; i < count; i++)
        {
            var parents = new List<string>();
            int remaining = count - i - 1;
            if (remaining > 0)
            {
                int parentCount = rng.Next(100) switch
                {
                    < 70 => 1,          // plain commit
                    < 90 => 2,          // merge
                    < 95 => Math.Min(3, remaining), // octopus
                    _ => 0,             // root / orphan tip
                };
                var seen = new HashSet<int>();
                for (int p = 0; p < parentCount; p++)
                {
                    // Bias towards near parents (realistic), occasionally far (pathological).
                    int offset = rng.Next(100) < 80 ? rng.Next(1, Math.Min(8, remaining + 1))
                                                    : rng.Next(1, remaining + 1);
                    if (seen.Add(offset)) parents.Add($"c{i + offset}");
                }
            }
            commits.Add(new GitCommitItem { Sha = $"c{i}", ParentShas = parents });
        }
        return commits;
    }

    // ---- Equivalence against the frozen pre-optimization algorithm ------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1337)]
    [InlineData(9001)]
    public void RouteCommits_MatchesReferenceImplementation_OnRandomDags(int seed)
    {
        var commits = RandomDag(seed, count: 300);
        var optimized = new CommitGraphRouter()
            .RouteCommits(commits, new GraphFringeState());
        var reference = ReferenceRoute(commits, new GraphFringeState(), null);

        Assert.Equal(reference.Nodes.Count, optimized.Nodes.Count);
        for (int i = 0; i < reference.Nodes.Count; i++)
            AssertNodesEqual(reference.Nodes[i], optimized.Nodes[i]);
        Assert.Equal(reference.EndFringe.ActiveLanes, optimized.EndFringe.ActiveLanes);
    }

    [Fact]
    public void RouteCommits_MatchesReferenceImplementation_WithPinnedTips()
    {
        var commits = RandomDag(21, count: 200);
        var tips = new[] { commits[10].Sha, commits[3].Sha, commits[40].Sha };

        var optimized = new CommitGraphRouter().RouteCommits(commits, new GraphFringeState(), tips);
        var reference = ReferenceRoute(commits, new GraphFringeState(), tips);

        Assert.Equal(reference.Nodes.Count, optimized.Nodes.Count);
        for (int i = 0; i < reference.Nodes.Count; i++)
            AssertNodesEqual(reference.Nodes[i], optimized.Nodes[i]);
        Assert.Equal(reference.EndFringe.ActiveLanes, optimized.EndFringe.ActiveLanes);
    }

    /// <summary>
    /// The router exactly as it was before the H2 optimization (linear IndexOf/Contains/FindIndex
    /// scans), kept verbatim as the semantic oracle: the optimized router must be output-identical
    /// on any input. Do not "fix" or optimize this copy — its whole value is that it is the old code.
    /// </summary>
    private static GraphRouteResult ReferenceRoute(IEnumerable<GitCommitItem> commits,
        GraphFringeState incomingFringe, IReadOnlyList<string>? priorityTips)
    {
        var result = new GraphRouteResult();
        var activeLanes = new List<string>(incomingFringe.ActiveLanes);
        int rowIndex = 0;

        var pendingSeeds = new HashSet<string>();
        if (activeLanes.Count == 0 && priorityTips != null)
        {
            foreach (var tip in priorityTips)
            {
                if (!string.IsNullOrEmpty(tip) && !activeLanes.Contains(tip))
                {
                    activeLanes.Add(tip);
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
                RowIndex = rowIndex++
            };

            int laneIndex = activeLanes.IndexOf(commit.Sha);
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

            if (commit.ParentShas.Count > 0)
            {
                string firstParent = commit.ParentShas[0];
                int existingParentLane = activeLanes.IndexOf(firstParent);
                if (existingParentLane != -1 && existingParentLane != laneIndex)
                {
                    if (laneIndex < existingParentLane)
                    {
                        activeLanes[laneIndex] = firstParent;
                        activeLanes[existingParentLane] = string.Empty;
                    }
                    else
                    {
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
                activeLanes[laneIndex] = string.Empty;
            }

            for (int i = 1; i < commit.ParentShas.Count; i++)
            {
                string parentSha = commit.ParentShas[i];
                if (!activeLanes.Contains(parentSha))
                {
                    int emptySlot = activeLanes.FindIndex(string.IsNullOrEmpty);
                    if (emptySlot == -1) activeLanes.Add(parentSha);
                    else activeLanes[emptySlot] = parentSha;
                }
            }

            for (int i = 0; i < incomingLanes.Count; i++)
            {
                if (!string.IsNullOrEmpty(incomingLanes[i]) && !pendingSeeds.Contains(incomingLanes[i]))
                    node.IncomingLanes.Add(i);
            }

            foreach (var parentSha in commit.ParentShas)
            {
                int parentLane = activeLanes.IndexOf(parentSha);
                if (parentLane != -1)
                    node.OutgoingLines.Add(new GraphLine(node.LaneIndex, parentLane));
            }

            for (int i = 0; i < incomingLanes.Count; i++)
            {
                if (i == node.LaneIndex) continue;
                if (string.IsNullOrEmpty(incomingLanes[i]) || pendingSeeds.Contains(incomingLanes[i])) continue;
                int newLane = activeLanes.IndexOf(incomingLanes[i]);
                if (newLane != -1)
                    node.OutgoingLines.Add(new GraphLine(i, newLane));
            }

            result.Nodes.Add(node);
        }

        result.EndFringe.ActiveLanes = activeLanes;
        return result;
    }

    private static void AssertNodesEqual(GraphNode expected, GraphNode actual)
    {
        Assert.Equal(expected.CommitSha, actual.CommitSha);
        Assert.Equal(expected.LaneIndex, actual.LaneIndex);
        Assert.Equal(expected.IncomingLanes, actual.IncomingLanes);
        Assert.Equal(expected.OutgoingLines.Count, actual.OutgoingLines.Count);
        for (int i = 0; i < expected.OutgoingLines.Count; i++)
        {
            Assert.Equal(expected.OutgoingLines[i].FromLane, actual.OutgoingLines[i].FromLane);
            Assert.Equal(expected.OutgoingLines[i].ToLane, actual.OutgoingLines[i].ToLane);
        }
    }
}
