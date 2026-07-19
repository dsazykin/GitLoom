using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// A subscribed source of bot-authored pull requests (P2-12): a repo on a host, optionally narrowed to a
/// specific author. When <see cref="AuthorFilter"/> is null the intake's configurable default bot list
/// applies. Vendor-neutral: Codex/Jules/Copilot only ever surface PRs, and this subscribes those PRs into
/// the same verify → review → merge pipeline.
/// </summary>
public sealed record ExternalPrSource(string Host, string Owner, string Repo, string? AuthorFilter)
{
    /// <summary>The stable key (<c>host/owner/repo</c>) the seen-head store groups PRs under.</summary>
    public string Key => $"{Host}/{Owner}/{Repo}";
}

/// <summary>
/// External agent PR intake (P2-12, daemon). Polls subscribed sources for open bot-authored PRs through
/// the ONE audited T-23 transport (<see cref="IPullRequestService"/>), materializes each new/updated PR
/// head as an <c>agent/pr-&lt;n&gt;</c> merge-queue entry (fetch → worktree → <c>Working</c>), and lets the
/// P2-10 queue verify it exactly as a local agent. Merge is routed back through the host PR merge API by
/// <see cref="MergeDispatch"/>, never a local foreground merge.
///
/// <para>INVARIANTS: the intake writes NOTHING upstream without an explicit user action — it only ever
/// calls the read (list) surface of the transport (invariant 1); all host traffic stays inside the T-23
/// transport (invariant 2); external entries obey the same <c>CanMerge</c> gates as local branches
/// (invariant 3, inherited by entering the same queue).</para>
/// </summary>
public interface IExternalPrIntake
{
    /// <summary>Persists a source to poll. Duplicate <c>(host, owner, repo, filter)</c> subscribe is idempotent.</summary>
    void Subscribe(ExternalPrSource source);

    /// <summary>Poll: new/updated open PRs matching the filter → materialize each as a queue entry
    /// (fetch PR head into the VM bare repo as agent/pr-&lt;n&gt;, worktree, enter MergeQueue at Working);
    /// PRs closed upstream → cancel + prune. Rate limits back off through the host client's typed error.</summary>
    Task PollOnceAsync(CancellationToken ct);

    /// <summary>The daemon scheduler loop: poll on the configured interval until cancelled (P2-12).
    /// A poll never throws a rate limit (caught + backed off), so the loop never crashes.</summary>
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// Materializes a PR head into an agent worktree (P2-12 step 2). The production implementation fetches
/// <c>pull/&lt;n&gt;/head</c> from the host into the agent worktree and hard-resets it (the daemon
/// provisioning-plane fetch — the quarantine rule cuts the <i>agent</i> worktree off from the real remote,
/// not this daemon-side provisioning fetch). Returns the resulting head SHA; a moved SHA drives a
/// re-materialize. This is a git-CLI seam — <b>no HTTP transport</b> (host API traffic stays in T-23).
/// </summary>
public interface IPrHeadFetcher
{
    /// <summary>Fetch/refresh the PR head into <c>agent/&lt;agentId&gt;</c> and return its current head SHA.</summary>
    Task<string> FetchHeadAsync(ExternalPrSource source, string repoHash, string agentId, int prNumber, CancellationToken ct);
}

/// <summary>Resolves an <see cref="ExternalPrSource"/> to the daemon objects a poll needs.</summary>
/// <param name="RepoPath">The local repo path the T-23 <see cref="IPullRequestService"/> resolves host + token from.</param>
/// <param name="RepoHash">The P2-06 repo hash keying the bare mirror, worktrees, and queue.</param>
/// <param name="Queue">The repo's live <see cref="MergeQueue"/> the PR enters.</param>
public sealed record PrIntakeTarget(string RepoPath, string RepoHash, MergeQueue Queue);

/// <inheritdoc cref="IExternalPrIntake"/>
public sealed class ExternalPrIntake : IExternalPrIntake
{
    /// <summary>The default bot authors an unfiltered source polls for (configurable via <see cref="AuthorFilters"/>).</summary>
    public static readonly IReadOnlyList<string> DefaultBotAuthors =
        new[] { "codex[bot]", "google-jules[bot]", "copilot" };

    private readonly IPullRequestService _prService;
    private readonly IPrIntakeStore _store;
    private readonly IAgentWorktreeManager _worktrees;
    private readonly IPrHeadFetcher _fetcher;
    private readonly Func<ExternalPrSource, PrIntakeTarget?> _resolveTarget;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, int Attempt)> _backoff =
        new(StringComparer.Ordinal);

    /// <summary>The configurable bot-author allow-list for sources without their own <c>AuthorFilter</c>.</summary>
    public IReadOnlyList<string> AuthorFilters { get; set; } = DefaultBotAuthors;

    /// <summary>The poll cadence for the daemon scheduler loop (<see cref="RunAsync"/>).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The first rate-limit backoff delay; each consecutive rate-limit doubles it up to <see cref="MaxBackoff"/>.</summary>
    public TimeSpan BaseBackoff { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The backoff ceiling — a persistent rate limit never spins tighter than this.</summary>
    public TimeSpan MaxBackoff { get; set; } = TimeSpan.FromMinutes(15);

    public ExternalPrIntake(
        IPullRequestService prService,
        IPrIntakeStore store,
        IAgentWorktreeManager worktrees,
        IPrHeadFetcher fetcher,
        Func<ExternalPrSource, PrIntakeTarget?> resolveTarget,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null)
    {
        _prService = prService ?? throw new ArgumentNullException(nameof(prService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _worktrees = worktrees ?? throw new ArgumentNullException(nameof(worktrees));
        _fetcher = fetcher ?? throw new ArgumentNullException(nameof(fetcher));
        _resolveTarget = resolveTarget ?? throw new ArgumentNullException(nameof(resolveTarget));
        _audit = audit ?? new InMemoryAuditLog();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public void Subscribe(ExternalPrSource source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        // Idempotent: the store dedupes on (host, owner, repo, filter) — a repeat subscribe adds no row.
        _store.AddSubscription(source);
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        foreach (var source in _store.Subscriptions())
        {
            ct.ThrowIfCancellationRequested();
            await PollSourceAsync(source, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The daemon scheduler loop: poll on <see cref="PollInterval"/> until cancelled. One poll at a
    /// time; a poll never throws a rate limit (it is caught and backed off), so the loop never crashes.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollSourceAsync(ExternalPrSource source, CancellationToken ct)
    {
        // Honour any active rate-limit backoff for this source (edge row 4 — never a tight retry loop).
        lock (_gate)
        {
            if (_backoff.TryGetValue(source.Key, out var b) && _clock() < b.Until)
            {
                return;
            }
        }

        var target = _resolveTarget(source);
        if (target is null)
        {
            return; // the repo isn't mounted this poll — leave the subscription for a later cycle.
        }

        IReadOnlyList<PullRequestItem> prs;
        try
        {
            prs = await _prService.ListAsync(target.RepoPath, PullRequestState.Open, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRateLimit(ex))
        {
            RecordBackoff(source);
            return; // poller stays alive; the next allowed poll is delayed.
        }

        ClearBackoff(source);

        var openNumbers = prs.Select(p => p.Number).ToHashSet();

        // Materialize/refresh each matching open PR.
        foreach (var pr in prs.Where(p => MatchesAuthor(p, source)))
        {
            ct.ThrowIfCancellationRequested();
            await MaterializeAsync(source, target, pr, ct).ConfigureAwait(false);
        }

        // Clean up any tracked PR that is no longer open upstream (closed/merged mid-queue — edge row 2).
        foreach (var tracked in _store.TrackedPrNumbers(source.Key))
        {
            if (openNumbers.Contains(tracked))
            {
                continue;
            }

            var agentId = AgentIdFor(tracked);
            target.Queue.Cancel(agentId);
            TryRemoveWorktree(target.RepoHash, agentId);
            _store.Untrack(source.Key, tracked);
            _audit.Append(new AuditEvent("external_pr_closed", new Dictionary<string, string>
            {
                ["source"] = source.Key,
                ["pr"] = tracked.ToString(),
                ["agent"] = agentId,
            }));
        }
    }

    private async Task MaterializeAsync(ExternalPrSource source, PrIntakeTarget target, PullRequestItem pr, CancellationToken ct)
    {
        var agentId = AgentIdFor(pr.Number);
        var seen = _store.GetSeenHead(source.Key, pr.Number);

        if (seen is null)
        {
            // New PR: create the worktree, fetch the head, enter the queue at Working as an External entry.
            _worktrees.CreateAgentWorktree(target.RepoHash, agentId);
            var head = await _fetcher.FetchHeadAsync(source, target.RepoHash, agentId, pr.Number, ct).ConfigureAwait(false);
            target.Queue.EnsureEntry(agentId, MergeEntryOrigin.External);
            _store.SetSeenHead(source.Key, pr.Number, head);
            _audit.Append(new AuditEvent("external_pr_materialized", new Dictionary<string, string>
            {
                ["source"] = source.Key,
                ["pr"] = pr.Number.ToString(),
                ["agent"] = agentId,
                ["head"] = head,
            }));
            return;
        }

        // Existing PR: refresh the worktree head and detect a moved head (a force-push is just a head move
        // whose old SHA disappears — edge row 1).
        var newHead = await _fetcher.FetchHeadAsync(source, target.RepoHash, agentId, pr.Number, ct).ConfigureAwait(false);
        if (string.Equals(newHead, seen, StringComparison.Ordinal))
        {
            return; // unchanged — idempotent; no re-queue, no duplicate worktree.
        }

        // Head moved: invalidate the stale verification and re-enter Working (identical to local agents).
        target.Queue.NotifyNewCommits(agentId);
        _store.SetSeenHead(source.Key, pr.Number, newHead);
        _audit.Append(new AuditEvent("external_pr_head_moved", new Dictionary<string, string>
        {
            ["source"] = source.Key,
            ["pr"] = pr.Number.ToString(),
            ["agent"] = agentId,
            ["head"] = newHead,
        }));
    }

    // ---- Author filter (configurable; per-source override wins over the default bot list) ----

    /// <summary>True iff the PR author matches this source's filter (its own, else the default bot list). Case-insensitive.</summary>
    public bool MatchesAuthor(PullRequestItem pr, ExternalPrSource source)
    {
        var filters = string.IsNullOrWhiteSpace(source.AuthorFilter)
            ? AuthorFilters
            : new[] { source.AuthorFilter! };

        return filters.Any(f => string.Equals(f, pr.Author, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Rate-limit backoff --------------------------------------------------

    // The host client maps a rate limit to a typed GitOperationException naming "rate limit" (G-4 scrubbed).
    private static bool IsRateLimit(Exception ex) =>
        ex is GitOperationException && ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    private void RecordBackoff(ExternalPrSource source)
    {
        lock (_gate)
        {
            var attempt = _backoff.TryGetValue(source.Key, out var b) ? b.Attempt + 1 : 1;
            var delayTicks = Math.Min(
                BaseBackoff.Ticks * (long)Math.Pow(2, attempt - 1),
                MaxBackoff.Ticks);
            _backoff[source.Key] = (_clock() + TimeSpan.FromTicks(delayTicks), attempt);
        }

        _audit.Append(new AuditEvent("external_pr_rate_limited", new Dictionary<string, string>
        {
            ["source"] = source.Key,
        }));
    }

    private void ClearBackoff(ExternalPrSource source)
    {
        lock (_gate)
        {
            _backoff.Remove(source.Key);
        }
    }

    /// <summary>The time (per source) before which the next poll is skipped due to rate-limit backoff, if any.</summary>
    public DateTimeOffset? BackoffUntil(ExternalPrSource source)
    {
        lock (_gate)
        {
            return _backoff.TryGetValue(source.Key, out var b) ? b.Until : null;
        }
    }

    private void TryRemoveWorktree(string repoHash, string agentId)
    {
        try
        {
            // Force: the branch is gone upstream, discard any local tree; this also deletes agent/<id>.
            _worktrees.RemoveAgentWorktree(repoHash, agentId, force: true);
        }
        catch
        {
            // Best-effort cleanup — a missing/already-pruned worktree must not fail the poll.
        }
    }

    private static string AgentIdFor(int prNumber) => $"pr-{prNumber}";
}
