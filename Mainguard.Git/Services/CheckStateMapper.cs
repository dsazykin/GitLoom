using System;
using System.Collections.Generic;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// The pure, unit-pinned roll-up at the heart of CI/checks status (T-26). It maps a host's raw
/// <c>status</c>+<c>conclusion</c> (check-runs) or <c>state</c> (legacy commit statuses) to the
/// UI-facing <see cref="CheckState"/>, and reduces a set of run states to one <see cref="CheckState.Overall"/>
/// verdict. No IO, no host types, no time — every branch below is fixed by a unit test so the badge can
/// never drift from these rules.
///
/// <para><b>Overall rule (pinned):</b> <see cref="CheckState.Failure"/> if <i>any</i> run failed, else
/// <see cref="CheckState.Pending"/> if <i>any</i> run is still pending, else <see cref="CheckState.Success"/>.
/// <see cref="CheckState.Neutral"/> (skipped/neutral) never fails or pends a commit — it is ignored — so a
/// neutral-only set rolls up to Success, and an empty set also rolls up to Success (but the commit reports
/// <see cref="CommitChecks.HasAny"/>=false so the badge is hidden, i.e. treated as "no CI", not a pass).</para>
/// </summary>
public static class CheckStateMapper
{
    /// <summary>
    /// Maps a GitHub Actions / app check-run to a <see cref="CheckState"/> from its <paramref name="status"/>
    /// (<c>queued</c>/<c>in_progress</c>/<c>completed</c>) and, once completed, its <paramref name="conclusion"/>.
    /// A not-yet-completed run is Pending regardless of conclusion; a completed run maps its conclusion —
    /// <c>action_required</c>/<c>timed_out</c>/<c>cancelled</c> count as Failure, <c>skipped</c>/<c>neutral</c>
    /// (and any unrecognized/absent conclusion) as Neutral.
    /// </summary>
    public static CheckState FromCheckRun(string? status, string? conclusion)
    {
        var s = (status ?? "").Trim().ToLowerInvariant();
        if (s is "queued" or "in_progress" or "pending" or "waiting" or "requested" or "")
            return CheckState.Pending;

        // status == "completed" (or anything else terminal) → decide on the conclusion.
        return FromConclusion(conclusion);
    }

    private static CheckState FromConclusion(string? conclusion) => (conclusion ?? "").Trim().ToLowerInvariant() switch
    {
        "success" => CheckState.Success,
        "failure" => CheckState.Failure,
        "timed_out" => CheckState.Failure,
        "cancelled" => CheckState.Failure,
        "action_required" => CheckState.Failure,
        "startup_failure" => CheckState.Failure,
        "stale" => CheckState.Failure,
        "neutral" => CheckState.Neutral,
        "skipped" => CheckState.Neutral,
        _ => CheckState.Neutral, // unknown/absent conclusion on a completed run — never treat as a pass or a fail
    };

    /// <summary>
    /// Maps a legacy combined commit-status <c>state</c> (<c>success</c>/<c>pending</c>/<c>failure</c>/<c>error</c>)
    /// to a <see cref="CheckState"/>. <c>error</c> is a Failure; an unknown/absent state is Pending (safest:
    /// never silently passes).
    /// </summary>
    public static CheckState FromLegacyStatus(string? state) => (state ?? "").Trim().ToLowerInvariant() switch
    {
        "success" => CheckState.Success,
        "failure" => CheckState.Failure,
        "error" => CheckState.Failure,
        "pending" => CheckState.Pending,
        _ => CheckState.Pending,
    };

    /// <summary>Reduces a set of run states to one overall verdict per the pinned rule (see the type doc).</summary>
    public static CheckState Overall(IEnumerable<CheckState> states)
    {
        if (states is null) throw new ArgumentNullException(nameof(states));
        bool anyPending = false, anySuccess = false;
        foreach (var st in states)
        {
            switch (st)
            {
                case CheckState.Failure: return CheckState.Failure; // failure dominates — short-circuit
                case CheckState.Pending: anyPending = true; break;
                case CheckState.Success: anySuccess = true; break;
                case CheckState.Neutral: break;                     // ignored by the roll-up
            }
        }
        if (anyPending) return CheckState.Pending;
        _ = anySuccess; // success and neutral-only both fall through to Success (Neutral ignored)
        return CheckState.Success;
    }

    /// <summary>
    /// Builds the <see cref="CommitChecks"/> for a commit from its runs: the overall roll-up plus the
    /// pass/fail/pending counts (Neutral runs are counted in none of them). An empty list yields a result
    /// whose <see cref="CommitChecks.HasAny"/> is false.
    /// </summary>
    public static CommitChecks Rollup(string sha, IReadOnlyList<CheckRunItem> runs)
    {
        if (runs is null) throw new ArgumentNullException(nameof(runs));

        int passed = 0, failed = 0, pending = 0;
        var states = new List<CheckState>(runs.Count);
        foreach (var r in runs)
        {
            states.Add(r.State);
            switch (r.State)
            {
                case CheckState.Success: passed++; break;
                case CheckState.Failure: failed++; break;
                case CheckState.Pending: pending++; break;
            }
        }

        return new CommitChecks
        {
            Sha = sha,
            Overall = Overall(states),
            Passed = passed,
            Failed = failed,
            Pending = pending,
            Runs = runs,
        };
    }
}
