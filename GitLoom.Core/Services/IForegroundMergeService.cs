namespace GitLoom.Core.Services;

/// <summary>The request for the human-gated foreground merge (P2-10 §3.5).</summary>
/// <param name="RepoPath">The Windows-side working repo.</param>
/// <param name="RepoHash">The P2-06 repo hash (drives <c>ResolveSyncRemote</c> and the merge lease).</param>
/// <param name="AgentId">The agent whose <c>agent/&lt;id&gt;</c> branch is merged.</param>
/// <param name="ExpectedMainSha">The <c>main@sha</c> the verification ran against (the A5 CAS old-OID).</param>
/// <param name="MainBranch">The local main branch the merge lands on.</param>
/// <param name="AllowStaleOverride">Loud, separate override path: merge a stale/unverified branch anyway (journaled + audited).</param>
/// <param name="OverrideReason">Why the override was used (recorded in the audit event).</param>
public sealed record ForegroundMergeRequest(
    string RepoPath,
    string RepoHash,
    string AgentId,
    string ExpectedMainSha,
    string MainBranch = "main",
    bool AllowStaleOverride = false,
    string? OverrideReason = null);

/// <summary>The outcome of a foreground merge attempt.</summary>
/// <param name="Merged">True iff the merge landed on main.</param>
/// <param name="NewMainSha">The post-merge <c>main@sha</c> when merged.</param>
/// <param name="CasLost">True when the A5 ref-level compare-and-swap lost (main moved) — no merge happened.</param>
/// <param name="Reason">A human-readable reason when not merged.</param>
public sealed record ForegroundMergeResult(bool Merged, string? NewMainSha, bool CasLost, string? Reason);

/// <summary>
/// The Windows-side, human-gated "Merge to Main" (P2-10 §3.5). Fetches the SC-2-resolved sync remote,
/// then merges <c>agent/&lt;id&gt;</c> onto main under a ref-level compare-and-swap on
/// <c>refs/heads/main</c> (A5) — journaled via T-19 so it is undoable. The merge is the RT-D1 two-step
/// daemon conversation (<c>BeginMerge</c> lease → journaled merge → <c>ConfirmMerge</c>). There is no
/// auto-merge: this is the sole path to <c>Merged</c>.
/// </summary>
public interface IForegroundMergeService
{
    /// <summary>Runs the full begin → merge → confirm conversation and returns the outcome.</summary>
    ForegroundMergeResult MergeAgentBranch(ForegroundMergeRequest request);
}
