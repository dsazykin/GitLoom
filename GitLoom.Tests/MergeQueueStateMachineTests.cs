using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;
using VerificationRecord = GitLoom.Core.Agents.Orchestrator.VerificationRecord;

namespace GitLoom.Tests;

/// <summary>
/// P2-10 merge-queue state machine — the densest suite of the milestone (plan §6 tests 1,3,5,6,14 +
/// TI-P2-10 1–6). Exhaustive legal transitions, typed-illegal transitions, the stale cascade + FIFO
/// re-queue, the loud override, immutable records, restart resume, and the NoAutoMergePathExists shape.
/// </summary>
public class MergeQueueStateMachineTests
{
    private sealed class Harness
    {
        public InMemoryMergeQueueStore StateStore = new();
        public InMemoryVerificationStore VerStore = new();
        public InMemoryAuditLog Audit = new();
        public ChangedTestCommandGate ChangedGate = new();
        public List<string> RequeueOrder = new();
        public MergeQueue Queue = null!;
        private long _tick;
        private readonly HashSet<string> _fails = new(StringComparer.Ordinal);

        public void FailFor(string agentId) => _fails.Add(agentId);

        public MergeQueue Build(bool withRequeue = true, bool withChangedGate = false)
        {
            MergeQueue queue = null!;
            Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            {
                var when = DateTimeOffset.UnixEpoch.AddSeconds(Interlocked.Increment(ref _tick));
                var passed = !_fails.Contains(id);
                return Task.FromResult(new VerificationRecord(
                    id, queue.CurrentMainSha, passed, "log.txt", "npm test", "confighash", when));
            };

            // withRequeue: re-verify (records order). Otherwise a no-op requeue so stale states stay put
            // for the gate/override/immutability assertions (production always re-verifies).
            Func<string, CancellationToken, Task> requeue = withRequeue
                ? (id, ct) => { RequeueOrder.Add(id); return queue.RunVerificationAsync(id, ct); }
            : (id, ct) => Task.CompletedTask;

            var gates = withChangedGate ? new IMergeGate[] { ChangedGate } : Array.Empty<IMergeGate>();
            queue = new MergeQueue("repo", "sha0", StateStore, VerStore, run, requeue, gates, Audit);
            Queue = queue;
            return queue;
        }
    }

    private static async Task<MergeQueue> VerifiedAsync(Harness h, string agentId)
    {
        await h.Queue.RunVerificationAsync(agentId, CancellationToken.None);
        Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState(agentId));
        return h.Queue;
    }

    // ---- Legal transitions ----------------------------------------------

    [Fact]
    public async Task RunVerification_Working_To_Verified_On_Pass()
    {
        var h = new Harness();
        h.Build();
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("a"));

        var record = await h.Queue.RunVerificationAsync("a", CancellationToken.None);

        Assert.True(record.Passed);
        Assert.Equal("sha0", record.MainSha);
        Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState("a"));
    }

    [Fact]
    public async Task RunVerification_Fail_ReturnsToWorking_NotSilentlyRetried()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        h.FailFor("a");

        var record = await h.Queue.RunVerificationAsync("a", CancellationToken.None);

        Assert.False(record.Passed);
        Assert.Equal(WorkerMergeState.Working, h.Queue.GetState("a"));
    }

    [Fact]
    public async Task Verified_To_AwaitingReview_To_Merged()
    {
        var h = new Harness();
        h.Build();
        await VerifiedAsync(h, "a");

        h.Queue.RequestReview("a");
        Assert.Equal(WorkerMergeState.AwaitingReview, h.Queue.GetState("a"));

        h.Queue.ConfirmHumanMerge("a", "sha1");
        Assert.Equal(WorkerMergeState.Merged, h.Queue.GetState("a"));
    }

    // ---- Typed-illegal transitions --------------------------------------

    [Fact]
    public void RequestReview_FromWorking_Throws_Typed()
    {
        var h = new Harness();
        h.Build();
        var ex = Assert.Throws<InvalidMergeStateTransitionException>(() => h.Queue.RequestReview("a"));
        Assert.Equal(WorkerMergeState.Working, ex.From);
        Assert.Equal(WorkerMergeState.AwaitingReview, ex.To);
    }

    [Fact]
    public void Reject_FromWorking_Throws_Typed()
    {
        var h = new Harness();
        h.Build();
        Assert.Throws<InvalidMergeStateTransitionException>(() => h.Queue.Reject("a"));
    }

    [Fact]
    public async Task Merged_IsTerminal_NoFurtherTransition()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        h.Queue.ConfirmHumanMerge("a", "sha1");

        // Any legal-looking op on a terminal branch throws (Merged has no outgoing transitions).
        Assert.Throws<InvalidMergeStateTransitionException>(() => h.Queue.RequestReview("a"));
    }

    // ---- Property test: random legal sequences never corrupt state ------

    [Fact]
    public async Task RandomLegalSequences_NeverCorruptState()
    {
        var rng = new Random(1234);
        for (var iter = 0; iter < 200; iter++)
        {
            var h = new Harness();
            h.Build(withRequeue: false);
            var id = "agent";
            for (var step = 0; step < 12; step++)
            {
                var state = h.Queue.GetState(id);
                // Pick only a legal operation for the current state.
                switch (state)
                {
                    case WorkerMergeState.Working:
                    case WorkerMergeState.StaleVerified:
                        await h.Queue.RunVerificationAsync(id, CancellationToken.None);
                        break;
                    case WorkerMergeState.Verified:
                        if (rng.Next(2) == 0) h.Queue.RequestReview(id);
                        else h.Queue.NotifyMainMoved("sha" + step);
                        break;
                    case WorkerMergeState.AwaitingReview:
                        if (rng.Next(2) == 0) h.Queue.Reject(id);
                        else h.Queue.NotifyMainMoved("sha" + step);
                        break;
                    default:
                        break; // terminal
                }

                Assert.True(Enum.IsDefined(typeof(WorkerMergeState), h.Queue.GetState(id)));
            }
        }
    }

    // ---- Stale cascade + FIFO re-queue ----------------------------------

    [Fact]
    public async Task NotifyMainMoved_FlipsAllVerifiedToStale_AndRequeuesFIFO()
    {
        var h = new Harness();
        h.Build();

        // Verify in the order C, A, B → their verification times order C < A < B (not alphabetical).
        await VerifiedAsync(h, "C");
        await VerifiedAsync(h, "A");
        await VerifiedAsync(h, "B");

        h.Queue.NotifyMainMoved("sha1");
        await h.Queue.LastCascade;

        // FIFO by original verification time.
        Assert.Equal(new[] { "C", "A", "B" }, h.RequeueOrder);

        // After the cascade each branch is re-verified against the NEW main.
        foreach (var id in new[] { "A", "B", "C" })
        {
            Assert.Equal(WorkerMergeState.Verified, h.Queue.GetState(id));
            Assert.Equal("sha1", h.VerStore.Latest("repo", id)!.MainSha);
        }
    }

    [Fact]
    public async Task StaleVerified_BlocksCanMerge_WithReason()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        Assert.True(h.Queue.CanMerge("a", out _));

        h.Queue.NotifyMainMoved("sha1"); // a is now stale (verified against sha0)
        Assert.Equal(WorkerMergeState.StaleVerified, h.Queue.GetState("a"));
        Assert.False(h.Queue.CanMerge("a", out var reason));
        Assert.Contains("stale", reason);
    }

    // ---- Override: logged + audited, CanMerge still false ---------------

    [Fact]
    public async Task Override_LoggedAudited_ButCanMergeStillFalse()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        h.Queue.NotifyMainMoved("sha1"); // stale

        h.Queue.RecordStaleOverrideUse("a", "human accepted the risk");

        Assert.Contains(h.Audit.Read(), e => e.Type == "stale_override_used");
        Assert.False(h.Queue.CanMerge("a", out _)); // override is a SEPARATE path
    }

    // ---- No test command: typed --------------------------------------

    [Fact]
    public async Task NoTestCommand_Throws_Typed_AndReturnsToWorking()
    {
        var store = new InMemoryMergeQueueStore();
        var ver = new InMemoryVerificationStore();
        Func<string, CancellationToken, Task<VerificationRecord>> run =
            (id, ct) => throw new NoVerificationCommandException("No verification command configured for this repository.");
        var queue = new MergeQueue("repo", "sha0", store, ver, run);

        await Assert.ThrowsAsync<NoVerificationCommandException>(() => queue.RunVerificationAsync("a", CancellationToken.None));
        Assert.Equal(WorkerMergeState.Working, queue.GetState("a"));
    }

    // ---- Immutable records ----------------------------------------------

    [Fact]
    public async Task VerificationRecord_Immutable_ReRunInsertsNewRow()
    {
        var h = new Harness();
        h.Build(withRequeue: false);
        await VerifiedAsync(h, "a");
        var first = h.VerStore.Latest("repo", "a")!;

        h.Queue.NotifyMainMoved("sha1"); // stale
        await h.Queue.RunVerificationAsync("a", CancellationToken.None); // re-verify → new row

        var history = h.VerStore.History("repo", "a");
        Assert.Equal(2, history.Count);
        Assert.Equal("sha0", history[0].MainSha);
        Assert.Equal("sha1", history[1].MainSha);
        // The first record is unchanged (immutability).
        Assert.Equal(first, history[0]);

        // The store API has no update method (compile-time immutability).
        Assert.Null(typeof(IVerificationStore).GetMethod("Update"));
    }

    // ---- Restart resume --------------------------------------------------

    [Fact]
    public async Task Restart_ResumesInterruptedVerifying_NeverStuck()
    {
        var store = new InMemoryMergeQueueStore();
        var ver = new InMemoryVerificationStore();

        // Simulate a daemon that crashed mid-Verifying: persist a Verifying row directly.
        store.Save(new Mainguard.Git.Models.MergeQueueRow
        {
            RepoHash = "repo",
            AgentId = "a",
            State = WorkerMergeState.Verifying.ToString(),
            UpdatedUtc = DateTime.UtcNow,
        });

        // A fresh queue hydrates from the store and resumes.
        MergeQueue queue = null!;
        Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            Task.FromResult(new VerificationRecord(id, queue.CurrentMainSha, true, "log", "cmd", "hash", DateTimeOffset.UtcNow));
        queue = new MergeQueue("repo", "sha0", store, ver, run);

        Assert.Equal(WorkerMergeState.Verifying, queue.GetState("a")); // resumed the interrupted state
        await queue.ResumeAfterRestartAsync();

        Assert.Equal(WorkerMergeState.Verified, queue.GetState("a")); // terminal state reached — not stuck
    }

    // ---- NoAutoMergePathExists (API shape) ------------------------------

    [Fact]
    public async Task NoAutoMergePathExists()
    {
        // 1. IMergeQueue's surface exposes no method that can reach Merged.
        var methods = typeof(IMergeQueue).GetMethods().Select(m => m.Name).ToArray();
        Assert.DoesNotContain("ConfirmHumanMerge", methods);
        Assert.DoesNotContain("Merge", methods);
        Assert.DoesNotContain("Confirm", methods);
        Assert.Equal(
            new[] { "GetState", "RunVerificationAsync", "NotifyMainMoved", "CanMerge" }.OrderBy(x => x),
            methods.OrderBy(x => x));

        // 2. Driving every IMergeQueue operation on a fresh Verified branch never yields Merged.
        var h = new Harness();
        IMergeQueue queue = h.Build(withRequeue: false);
        await queue.RunVerificationAsync("a", CancellationToken.None);
        queue.NotifyMainMoved("sha1");
        queue.CanMerge("a", out _);
        Assert.NotEqual(WorkerMergeState.Merged, queue.GetState("a"));
    }

    // ---- RT-D2: gamed test command flagged before a silent merge --------

    [Fact]
    public async Task GamedTestCommand_ShouldBeFlaggedBeforeSilentMerge()
    {
        var h = new Harness();
        h.Build(withRequeue: false, withChangedGate: true);
        await VerifiedAsync(h, "a");

        // RT-D2: the resolver detected the branch rewrote its test command vs the main baseline.
        var resolution = VerificationCommandResolver.Resolve(branchConfigContent: "exit 0", mainConfigContent: "npm test");
        Assert.True(resolution.ChangedVsMain);
        h.ChangedGate.SetFlagged("a", resolution.ChangedVsMain);

        // The merge is blocked with the dedicated reason until the item is acknowledged.
        Assert.False(h.Queue.CanMerge("a", out var reason));
        Assert.Contains("test command changed", reason);

        h.ChangedGate.Acknowledge("a");
        Assert.True(h.Queue.CanMerge("a", out _));
    }

    [Fact]
    public void VerificationCommandResolver_HashesConfig_AndDetectsDrift()
    {
        var same = VerificationCommandResolver.Resolve("npm test", "npm test");
        Assert.False(same.ChangedVsMain);
        Assert.Equal(VerificationCommandResolver.Sha256("npm test"), same.ConfigHash);

        var drift = VerificationCommandResolver.Resolve("exit 0", "npm test");
        Assert.True(drift.ChangedVsMain);

        // A human-owned command pin overrides branch config and is never flagged.
        var pinned = VerificationCommandResolver.Resolve("exit 0", "npm test", pinnedCommand: "npm run verify");
        Assert.False(pinned.ChangedVsMain);
        Assert.Equal(new[] { "npm", "run", "verify" }, pinned.Command);

        Assert.Throws<NoVerificationCommandException>(() => VerificationCommandResolver.Resolve(null, "npm test"));
    }
}
