using System;
using System.Collections.Generic;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;

namespace GitLoom.Core.Agents.Orchestrator;

/// <summary>The computed agent-branch-vs-main diff for the review cockpit: the branch name it ran for, the
/// resolved main branch, the raw unified diff, and the parsed <see cref="FilePatch"/> list.</summary>
public sealed record MergeBranchDiff(
    string Branch, string MainBranch, string UnifiedDiff, IReadOnlyList<FilePatch> Files);

/// <summary>The daemon-side bridge P2-47 #7 adds behind <c>MergeQueueService.GetMergeDiff</c>.</summary>
public interface IMergeBranchDiffService
{
    /// <summary>Compute the merge-base diff of an agent's branch against the mirror's main branch.</summary>
    MergeBranchDiff Compute(string repoHash, string agentId);
}

/// <summary>
/// Computes the review cockpit's merge diff (agent branch vs main) by reusing the existing Core git path:
/// the ONE audited git primitive (<c>git diff main...agent/&lt;id&gt;</c> in the daemon's bare mirror, via
/// <see cref="AgentGitCommand"/>) feeding the pure T-06 <see cref="PatchParser"/>. It introduces no new
/// diff algorithm — only the daemon-side bridge that hands the <see cref="ReviewCockpitContext"/> its
/// <c>MergeDiff</c>, which the <c>StreamQueue</c> projection doesn't carry.
///
/// <para>The three-dot range shows exactly what the branch changed since it diverged from main (main's own
/// later commits are excluded) — the right scope for reviewing an agent's work.</para>
/// </summary>
public sealed class MergeBranchDiffService : IMergeBranchDiffService
{
    private readonly IRepoProvisioner _repos;

    public MergeBranchDiffService(IRepoProvisioner repos)
    {
        _repos = repos ?? throw new ArgumentNullException(nameof(repos));
    }

    public MergeBranchDiff Compute(string repoHash, string agentId)
    {
        if (string.IsNullOrWhiteSpace(repoHash))
        {
            throw new ArgumentException("A repo hash is required.", nameof(repoHash));
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        var barePath = _repos.BareRepoPathFor(repoHash);
        var branch = "agent/" + agentId;
        var main = ResolveDefaultBranch(barePath);

        // git diff main...agent/<id>: the merge-base diff (what the branch added since it diverged).
        var unified = AgentGitCommand.Run(barePath, "diff", $"{main}...{branch}");
        return new MergeBranchDiff(branch, main, unified, PatchParser.Parse(unified));
    }

    private static string ResolveDefaultBranch(string barePath)
    {
        if (AgentGitCommand.TryRun(barePath, out var output, "symbolic-ref", "--short", "HEAD") == 0)
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }
}
