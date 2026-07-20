using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;
using VerificationRecord = Mainguard.Agents.Agents.Orchestrator.VerificationRecord;

namespace Mainguard.Tests;

/// <summary>
/// P2-12 merge-path dispatch (plan §6 test 6 / TI-P2-12 6): the pluggable merge step routes by the queue
/// entry's <see cref="MergeEntryOrigin"/> — a local agent through the foreground service, an external PR
/// through the host merge API — and BOTH fire the queue's <c>NotifyMainMoved</c> cascade.
/// </summary>
public class MergeDispatchTests
{
    private const string RepoPath = "/repo";
    private const string RepoHash = "hash0";

    /// <summary>Records the host merge call; everything else is an unused stub.</summary>
    private sealed class RecordingPrService : IPullRequestService
    {
        public int MergeCalls { get; private set; }
        public int LastMergedNumber { get; private set; }

        public Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, CancellationToken ct)
        {
            MergeCalls++;
            LastMergedNumber = number;
            return Task.FromResult(new PullRequestItem { Number = number, State = PullRequestState.Merged });
        }

        public bool IsSupported(string repoPath) => true;
        public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<PullRequestItem>>(Array.Empty<PullRequestItem>());
        public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct) => Task.FromResult(new PullRequestDetail());
        public Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct) => Task.FromResult(new PullRequestItem());
        public Task CloseAsync(string repoPath, int number, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<PullRequestReview>>(Array.Empty<PullRequestReview>());
        public Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct) => Task.FromResult<IReadOnlyList<ReviewComment>>(Array.Empty<ReviewComment>());
        public Task<PullRequestReview> SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct) => Task.FromResult(new PullRequestReview());
    }

    /// <summary>Mimics the real foreground service: on merge it fires the daemon-wired <c>onMerged</c>
    /// callback (→ <c>queue.ConfirmHumanMerge</c> → <c>NotifyMainMoved</c>), exactly as P2-10 does.</summary>
    private sealed class FakeForeground : IForegroundMergeService
    {
        private readonly Action<string, string> _onMerged;
        private readonly string _newSha;
        public bool Called { get; private set; }

        public FakeForeground(Action<string, string> onMerged, string newSha)
        {
            _onMerged = onMerged;
            _newSha = newSha;
        }

        public ForegroundMergeResult MergeAgentBranch(ForegroundMergeRequest request)
        {
            Called = true;
            _onMerged(request.AgentId, _newSha);
            return new ForegroundMergeResult(true, _newSha, CasLost: false, Reason: null);
        }
    }

    private static MergeQueue BuildVerifiedQueue(string agentId, MergeEntryOrigin origin, Action<string, string> onMerged)
    {
        var queue = new MergeQueue(
            RepoHash, "sha0",
            new InMemoryMergeQueueStore(),
            new InMemoryVerificationStore(),
            runVerification: (id, ct) => Task.FromResult(new VerificationRecord(
                id, "sha0", true, "log.txt", "npm test", "cfg", DateTimeOffset.UnixEpoch)),
            requeue: (id, ct) => Task.CompletedTask);

        queue.EnsureEntry(agentId, origin);
        queue.RunVerificationAsync(agentId, CancellationToken.None).GetAwaiter().GetResult();
        Assert.Equal(WorkerMergeState.Verified, queue.GetState(agentId));
        return queue;
    }

    [Fact]
    public async Task MergePathDispatch_ShouldUseHostApiForPrEntries_AndLocalForegroundForLocalAgents()
    {
        var notified = new List<string>();

        // ---- Local origin → foreground service; NotifyMainMoved via its onMerged wiring ----
        var localQueue = BuildVerifiedQueue("local", MergeEntryOrigin.Local, (id, sha) => { });
        Action<string, string> localOnMerged = (id, sha) =>
        {
            notified.Add(id);
            localQueue.ConfirmHumanMerge(id, sha); // fires NotifyMainMoved
        };
        var localForeground = new FakeForeground(localOnMerged, "sha-local-1");
        var localPr = new RecordingPrService();
        var localDispatch = new MergeDispatch(
            localForeground, localPr,
            resolveQueue: rh => rh == RepoHash ? localQueue : null,
            fetchMergedMainSha: (req, ct) => Task.FromResult("unused"));

        var localOutcome = await localDispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "local", "sha0"), CancellationToken.None);

        Assert.True(localOutcome.Merged);
        Assert.True(localForeground.Called);                 // routed to the foreground service
        Assert.Equal(0, localPr.MergeCalls);                 // NOT the host API
        Assert.Equal(WorkerMergeState.Merged, localQueue.GetState("local"));
        Assert.Equal("sha-local-1", localQueue.CurrentMainSha); // NotifyMainMoved fired
        Assert.Contains("local", notified);

        // ---- External origin → host merge API, then NotifyMainMoved after the merged sha lands ----
        var extQueue = BuildVerifiedQueue("pr-7", MergeEntryOrigin.External, (id, sha) => { });
        var extPr = new RecordingPrService();
        var extForeground = new FakeForeground((id, sha) => { }, "unused");
        var extDispatch = new MergeDispatch(
            extForeground, extPr,
            resolveQueue: rh => rh == RepoHash ? extQueue : null,
            fetchMergedMainSha: (req, ct) => Task.FromResult("sha-ext-1"));

        var extOutcome = await extDispatch.DispatchMergeAsync(
            new MergeDispatchRequest(RepoPath, RepoHash, "pr-7", "sha0", PrNumber: 7), CancellationToken.None);

        Assert.True(extOutcome.Merged);
        Assert.False(extForeground.Called);                  // NOT the foreground service
        Assert.Equal(1, extPr.MergeCalls);                   // routed to the host merge API
        Assert.Equal(7, extPr.LastMergedNumber);
        Assert.Equal(WorkerMergeState.Merged, extQueue.GetState("pr-7"));
        Assert.Equal("sha-ext-1", extQueue.CurrentMainSha);  // NotifyMainMoved fired for the external path too
    }
}
