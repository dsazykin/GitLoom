using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Security;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-08 test contract #1/#2 — the pure token bucket. Property-style over a controllable clock: refill
/// never exceeds capacity, grants never exceed what refilled over a window, estimate→actual settlement
/// conserves tokens, and two saturating waiters are served FIFO (no starvation). No wall-clock reads.
/// </summary>
public class TokenBucketTests
{
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public Func<DateTimeOffset> Get => () => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    [Fact]
    public void Refill_NeverExceedsCapacity()
    {
        var rng = new Random(1234);
        for (var trial = 0; trial < 200; trial++)
        {
            var clock = new FakeClock();
            var rpm = rng.Next(1, 500);
            var tpm = rng.Next(100, 500_000);
            var bucket = new TokenBucket(rpm, tpm, clock.Get);

            // Drain a random amount, then advance a random (possibly huge) time and re-check.
            bucket.TryAcquire(rng.Next(0, tpm), out _);
            clock.Advance(TimeSpan.FromSeconds(rng.Next(0, 10_000)));

            var (reqAvail, tokAvail) = bucket.Available;
            var (reqCap, tokCap) = bucket.Capacity;
            Assert.True(reqAvail <= reqCap + 1e-9, $"req {reqAvail} > cap {reqCap}");
            Assert.True(tokAvail <= tokCap + 1e-9, $"tok {tokAvail} > cap {tokCap}");
        }
    }

    [Fact]
    public void Grants_DoNotExceedRefillOverWindow()
    {
        var rng = new Random(99);
        for (var trial = 0; trial < 50; trial++)
        {
            var clock = new FakeClock();
            var rpm = rng.Next(10, 120);
            var tpm = rng.Next(1000, 100_000);
            var bucket = new TokenBucket(rpm, tpm, clock.Get);
            var perAcquire = Math.Max(1, tpm / 20);

            long grantedTokens = 0;
            var totalSeconds = 0.0;

            for (var step = 0; step < 100; step++)
            {
                // Greedily take everything available right now.
                while (bucket.TryAcquire(perAcquire, out _))
                {
                    grantedTokens += perAcquire;
                }

                var advance = rng.Next(1, 30);
                clock.Advance(TimeSpan.FromSeconds(advance));
                totalSeconds += advance;
            }

            // Upper bound: initial full bucket + everything the tokens/min rate could refill in the window.
            var ceiling = tpm + (tpm / 60.0 * totalSeconds) + perAcquire; // +1 grant slack for the last partial
            Assert.True(grantedTokens <= ceiling, $"granted {grantedTokens} exceeded refill ceiling {ceiling}");
        }
    }

    [Fact]
    public void Release_SettlesActuals_ConservesTokens()
    {
        var rng = new Random(7);
        for (var trial = 0; trial < 500; trial++)
        {
            var clock = new FakeClock(); // frozen — isolate settlement from refill
            var tpm = rng.Next(2000, 50_000);
            var bucket = new TokenBucket(1000, tpm, clock.Get);

            var estimate = rng.Next(0, tpm);
            var actual = rng.Next(0, tpm);

            var (_, tokBefore) = bucket.Available;
            Assert.True(bucket.TryAcquire(estimate, out var lease));
            bucket.Release(lease, actual);
            var (_, tokAfter) = bucket.Available;

            // Net token permits consumed equal the ACTUAL usage, regardless of the estimate.
            Assert.Equal(tokBefore - actual, tokAfter, precision: 6);
        }
    }

    [Fact]
    public async Task Waiters_GrantedFifo_NoStarvation()
    {
        var clock = new FakeClock();
        // Small capacity: 2 requests/min, 2000 tokens/min. Each acquire wants 1000 tokens.
        var bucket = new TokenBucket(2, 2000, clock.Get);

        // Drain the initial burst (2 request permits, 2000 tokens).
        Assert.True(bucket.TryAcquire(1000, out _));
        Assert.True(bucket.TryAcquire(1000, out _));
        Assert.False(bucket.TryAcquire(1000, out _)); // empty now

        // Two saturating consumers interleave their waits: A1, B1, A2, B2.
        var a1 = bucket.AcquireAsync(1000, CancellationToken.None);
        var b1 = bucket.AcquireAsync(1000, CancellationToken.None);
        var a2 = bucket.AcquireAsync(1000, CancellationToken.None);
        var b2 = bucket.AcquireAsync(1000, CancellationToken.None);
        Assert.Equal(4, bucket.QueueDepth);

        var completionOrder = new List<Task<BucketLease>>();
        foreach (var _ in Enumerable.Range(0, 4))
        {
            // ~30s refills one request permit AND 1000 tokens — exactly one grant per tick.
            clock.Advance(TimeSpan.FromSeconds(31));
            bucket.Pump();

            var done = await Task.WhenAny(new[] { a1, b1, a2, b2 }
                .Where(t => !completionOrder.Contains(t))
                .ToArray());
            completionOrder.Add(done);
        }

        // FIFO: exactly the enqueue order, and the two consumers alternate (neither starves).
        Assert.Equal(new[] { a1, b1, a2, b2 }, completionOrder);
        Assert.True(a1.IsCompletedSuccessfully && b2.IsCompletedSuccessfully);

        // Tickets are monotonic in grant order.
        var tickets = completionOrder.Select(t => t.Result.Ticket).ToArray();
        Assert.Equal(tickets.OrderBy(x => x), tickets);
    }

    [Fact]
    public async Task AcquireAsync_Cancellation_RemovesWaiter()
    {
        var clock = new FakeClock();
        var bucket = new TokenBucket(1, 1000, clock.Get);
        Assert.True(bucket.TryAcquire(1000, out _)); // drain

        using var cts = new CancellationTokenSource();
        var pending = bucket.AcquireAsync(1000, cts.Token);
        Assert.Equal(1, bucket.QueueDepth);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, bucket.QueueDepth);
    }

    [Fact]
    public void FromKeyHealth_SeedsFromCeilings_FallsBackWhenMissing()
    {
        var clock = new FakeClock();

        var seeded = TokenBucket.FromKeyHealth(
            new KeyHealth { RequestsPerMinute = 400, TokensPerMinute = 80_000 }, clock.Get);
        Assert.Equal((400.0, 80_000.0), seeded.Capacity);

        var floored = TokenBucket.FromKeyHealth(new KeyHealth(), clock.Get);
        Assert.Equal(
            ((double)TokenBucket.DefaultRequestsPerMinute, (double)TokenBucket.DefaultTokensPerMinute),
            floored.Capacity);
    }
}
