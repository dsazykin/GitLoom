using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>A request to merge a queue entry (P2-12). Origin is read from the queue, not passed in.</summary>
/// <param name="RepoPath">The local repo path (T-23 host/token resolution + the foreground merge).</param>
/// <param name="RepoHash">The P2-06 repo hash (resolves the queue + drives the merge lease).</param>
/// <param name="AgentId">The entry's agent id (<c>pr-&lt;n&gt;</c> for an external PR).</param>
/// <param name="ExpectedMainSha">The <c>main@sha</c> the verification ran against (the A5 CAS old-OID).</param>
/// <param name="MainBranch">The local main branch the merge lands on.</param>
/// <param name="PrNumber">The upstream PR number — REQUIRED for an external entry, ignored for a local one.</param>
/// <param name="Method">The host merge method for an external entry.</param>
/// <param name="AllowStaleOverride">Loud, separate stale-override path for a local foreground merge.</param>
/// <param name="OverrideReason">Why the override was used (audited).</param>
public sealed record MergeDispatchRequest(
    string RepoPath,
    string RepoHash,
    string AgentId,
    string ExpectedMainSha,
    string MainBranch = "main",
    int? PrNumber = null,
    PullRequestMergeMethod Method = PullRequestMergeMethod.Merge,
    bool AllowStaleOverride = false,
    string? OverrideReason = null);

/// <summary>The outcome of a dispatched merge (mirrors <see cref="ForegroundMergeResult"/> for both paths).</summary>
public sealed record MergeDispatchOutcome(bool Merged, string? NewMainSha, bool CasLost, string? Reason);

/// <summary>
/// The P2-12 pluggable merge step: routes a merge by the queue entry's <see cref="MergeEntryOrigin"/>.
/// A <see cref="MergeEntryOrigin.Local"/> entry merges via the existing Windows foreground merge
/// (<see cref="IForegroundMergeService"/>, P2-10); a <see cref="MergeEntryOrigin.External"/> entry merges
/// back through the host PR merge API (T-23) and then, once the merged SHA lands locally, fires the
/// queue's <c>NotifyMainMoved</c> stale cascade. Both origins end at the SAME cascade — the human review
/// gate is unchanged (P2-11 cockpit); this only swaps the transport that lands the merge.
/// </summary>
public interface IMergeDispatch
{
    /// <summary>Merges the entry via its origin's transport and returns the outcome.</summary>
    Task<MergeDispatchOutcome> DispatchMergeAsync(MergeDispatchRequest request, CancellationToken ct);
}

/// <inheritdoc cref="IMergeDispatch"/>
public sealed class MergeDispatch : IMergeDispatch
{
    private readonly IForegroundMergeService _foreground;
    private readonly IPullRequestService _prService;
    private readonly Func<string, MergeQueue?> _resolveQueue;
    private readonly Func<MergeDispatchRequest, CancellationToken, Task<string>> _fetchMergedMainSha;

    /// <param name="foreground">The P2-10 Windows foreground merge (local entries). Its own <c>onMerged</c>
    /// wiring fires the queue's <c>ConfirmHumanMerge</c> → <c>NotifyMainMoved</c>.</param>
    /// <param name="prService">The audited T-23 transport used for the host PR merge (external entries).</param>
    /// <param name="resolveQueue">Resolves a repo hash → its live <see cref="MergeQueue"/> (origin lookup + cascade).</param>
    /// <param name="fetchMergedMainSha">After a host merge, fetches main and returns its new SHA (the sha that "lands locally").</param>
    public MergeDispatch(
        IForegroundMergeService foreground,
        IPullRequestService prService,
        Func<string, MergeQueue?> resolveQueue,
        Func<MergeDispatchRequest, CancellationToken, Task<string>> fetchMergedMainSha)
    {
        _foreground = foreground ?? throw new ArgumentNullException(nameof(foreground));
        _prService = prService ?? throw new ArgumentNullException(nameof(prService));
        _resolveQueue = resolveQueue ?? throw new ArgumentNullException(nameof(resolveQueue));
        _fetchMergedMainSha = fetchMergedMainSha ?? throw new ArgumentNullException(nameof(fetchMergedMainSha));
    }

    public async Task<MergeDispatchOutcome> DispatchMergeAsync(MergeDispatchRequest request, CancellationToken ct)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));

        var queue = _resolveQueue(request.RepoHash)
            ?? throw new InvalidOperationException($"No active merge queue for repo '{request.RepoHash}'.");

        var origin = queue.GetOrigin(request.AgentId);
        return origin switch
        {
            MergeEntryOrigin.External => await MergeExternalAsync(queue, request, ct).ConfigureAwait(false),
            _ => MergeLocal(request),
        };
    }

    // Local: the existing foreground merge. NotifyMainMoved is fired by the foreground service's own
    // onMerged callback (daemon-wired to queue.ConfirmHumanMerge) — the dispatch does not double-fire it.
    private MergeDispatchOutcome MergeLocal(MergeDispatchRequest request)
    {
        var result = _foreground.MergeAgentBranch(new ForegroundMergeRequest(
            request.RepoPath,
            request.RepoHash,
            request.AgentId,
            request.ExpectedMainSha,
            request.MainBranch,
            request.AllowStaleOverride,
            request.OverrideReason));

        return new MergeDispatchOutcome(result.Merged, result.NewMainSha, result.CasLost, result.Reason);
    }

    // External: merge through the host PR API, then land the merged sha locally and fire the cascade.
    private async Task<MergeDispatchOutcome> MergeExternalAsync(MergeQueue queue, MergeDispatchRequest request, CancellationToken ct)
    {
        if (request.PrNumber is not int prNumber)
        {
            throw new InvalidOperationException(
                $"External entry '{request.AgentId}' has no PR number to merge through the host API.");
        }

        // The ONE audited transport performs the merge (an explicit user action — invariant 1 permits it).
        await _prService.MergeAsync(request.RepoPath, prNumber, request.Method, ct).ConfigureAwait(false);

        // The merged sha lands locally (fetch main), then the queue's stale cascade fires — identical to
        // the local path's terminal handling.
        var newMainSha = await _fetchMergedMainSha(request, ct).ConfigureAwait(false);
        queue.ConfirmHumanMerge(request.AgentId, newMainSha); // → Merged + NotifyMainMoved

        return new MergeDispatchOutcome(Merged: true, newMainSha, CasLost: false, Reason: null);
    }
}
