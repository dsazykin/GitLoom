using System;

namespace Mainguard.Git.Models;

/// <summary>
/// RT-D1 merge lease + idempotency record (P2-10, M7 exit gate). The foreground merge is a two-step
/// daemon conversation: <c>BeginMerge</c> takes a per-repo lease (freezing conflicting queue actions),
/// the Windows-side journaled merge runs, then <c>ConfirmMerge</c> writes the idempotency outcome and
/// releases the lease. A crash between the committed merge and <c>ConfirmMerge</c> is reconciled on
/// daemon boot (journal replay synthesizes the missing confirm) — exactly once or none. One row per
/// repo; the row survives across the merge so the boot reconcile can find an outstanding lease.
/// </summary>
public class MergeLeaseRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The repo the lease is held for (P2-06 repo hash). One outstanding lease per repo.</summary>
    public string RepoHash { get; set; } = string.Empty;

    /// <summary>A unique id for this merge attempt (idempotency key).</summary>
    public string LeaseId { get; set; } = string.Empty;

    /// <summary>The agent (branch) being merged under this lease.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The <c>main@sha</c> the merge was expected to fast-forward from (the verified sha).</summary>
    public string ExpectedMainSha { get; set; } = string.Empty;

    /// <summary>The local main branch the merge lands on (the boot reconcile reads its current tip).</summary>
    public string MainBranch { get; set; } = "main";

    /// <summary>True once <c>ConfirmMerge</c> has recorded the outcome; the lease is then released.</summary>
    public bool Confirmed { get; set; }

    /// <summary>The post-merge <c>main@sha</c> recorded at confirm time (drives <c>NotifyMainMoved</c>).</summary>
    public string? PostMergeSha { get; set; }

    /// <summary>When the lease was taken (UTC).</summary>
    public DateTime BeginUtc { get; set; }
}
