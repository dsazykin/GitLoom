using System;
using System.Collections.Generic;

namespace GitLoom.Core.Models;

/// <summary>
/// The rolled-up, UI-facing state of a single check or of a commit overall (T-26). Host-specific
/// status/conclusion strings are mapped to exactly one of these by the pure <c>CheckStateMapper</c>, so
/// no host dialect ever reaches the ViewModel or the badge.
/// </summary>
public enum CheckState
{
    /// <summary>Queued / in-progress — the run hasn't concluded yet.</summary>
    Pending,

    /// <summary>Concluded successfully.</summary>
    Success,

    /// <summary>Concluded in a way that fails the commit (failure / error / timed-out / cancelled / action-required).</summary>
    Failure,

    /// <summary>Concluded without a pass/fail verdict (skipped / neutral) — ignored by the overall roll-up.</summary>
    Neutral,
}

/// <summary>
/// One check on a commit (T-26): a GitHub Actions / app <c>check-run</c> or a legacy commit
/// <c>status</c>, normalized to the host-agnostic shape. <see cref="Id"/> is the numeric check-run id
/// used to re-request a run; it is <c>0</c> for a legacy commit status (which GitHub cannot re-run), so
/// the UI hides the per-run Re-run action when <see cref="Id"/> is not positive.
/// </summary>
public sealed class CheckRunItem
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    public CheckState State { get; init; }
    public string RawStatus { get; init; } = "";               // queued | in_progress | completed
    public string? Conclusion { get; init; }                   // success | failure | neutral | cancelled | timed_out | action_required | skipped
    public string DetailsUrl { get; init; } = "";              // "view logs" target
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>True when this run carries a re-requestable check-run id (a legacy commit status has none).</summary>
    public bool CanRerun => Id > 0;
}

/// <summary>
/// The full check picture for one commit (T-26): every run plus the pure roll-up. <see cref="Overall"/>
/// follows the pinned rule — <see cref="CheckState.Failure"/> dominates, else <see cref="CheckState.Pending"/>,
/// else <see cref="CheckState.Success"/> (Neutral is ignored). <see cref="HasAny"/> is false for a commit
/// with no checks at all, which the UI treats as "no CI" (badge hidden), never as a failure.
/// </summary>
public sealed class CommitChecks
{
    public string Sha { get; init; } = "";
    public CheckState Overall { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int Pending { get; init; }
    public IReadOnlyList<CheckRunItem> Runs { get; init; } = Array.Empty<CheckRunItem>();

    /// <summary>True when the commit has at least one check; false → no CI configured (badge hidden).</summary>
    public bool HasAny => Runs.Count > 0;

    /// <summary>An empty result for a commit that reports no checks (the graceful no-CI shape).</summary>
    public static CommitChecks None(string sha) => new() { Sha = sha, Overall = CheckState.Success };
}
