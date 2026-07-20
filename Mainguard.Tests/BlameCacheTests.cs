using System;
using System.Collections.Generic;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

// TI-11 (cache tier): the bounded-LRU invariant of the blame cache in isolation —
// eviction at capacity, recency ordering, per-repo invalidation. An unbounded blame
// cache is a §T-11 rejection trigger, so the capacity bound is asserted directly.
public class BlameCacheTests
{
    private static IReadOnlyList<BlameLine> Val(string sha) =>
        new[] { new BlameLine { LineNumber = 1, Sha = sha } };

    private static BlameCache.Key K(string repo, string path, string sha) => new(repo, path, sha);

    [Fact]
    public void Set_And_TryGet_ShouldRoundTrip()
    {
        var cache = new BlameCache(capacity: 4);
        var key = K("/repo", "a.cs", "sha1");

        Assert.False(cache.TryGet(key, out _));

        var value = Val("sha1");
        cache.Set(key, value);

        Assert.True(cache.TryGet(key, out var got));
        Assert.Same(value, got);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Set_ShouldEvictLeastRecentlyUsed_WhenOverCapacity()
    {
        var cache = new BlameCache(capacity: 2);
        var k1 = K("/repo", "1", "s1");
        var k2 = K("/repo", "2", "s2");
        var k3 = K("/repo", "3", "s3");

        cache.Set(k1, Val("s1"));
        cache.Set(k2, Val("s2"));
        cache.Set(k3, Val("s3"));   // over capacity → k1 (LRU) evicted

        Assert.Equal(2, cache.Count);
        Assert.False(cache.TryGet(k1, out _));
        Assert.True(cache.TryGet(k2, out _));
        Assert.True(cache.TryGet(k3, out _));
    }

    [Fact]
    public void TryGet_ShouldRefreshRecency_SoTouchedEntrySurvivesEviction()
    {
        var cache = new BlameCache(capacity: 2);
        var k1 = K("/repo", "1", "s1");
        var k2 = K("/repo", "2", "s2");
        var k3 = K("/repo", "3", "s3");

        cache.Set(k1, Val("s1"));
        cache.Set(k2, Val("s2"));
        Assert.True(cache.TryGet(k1, out _)); // k1 now most-recently-used, k2 is LRU
        cache.Set(k3, Val("s3"));             // evicts k2, not k1

        Assert.True(cache.TryGet(k1, out _));
        Assert.False(cache.TryGet(k2, out _));
        Assert.True(cache.TryGet(k3, out _));
    }

    [Fact]
    public void InvalidateRepo_ShouldDropOnlyThatReposEntries()
    {
        var cache = new BlameCache(capacity: 8);
        cache.Set(K("/repoA", "a", "s1"), Val("s1"));
        cache.Set(K("/repoA", "b", "s2"), Val("s2"));
        cache.Set(K("/repoB", "a", "s3"), Val("s3"));

        cache.InvalidateRepo("/repoA");

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet(K("/repoA", "a", "s1"), out _));
        Assert.False(cache.TryGet(K("/repoA", "b", "s2"), out _));
        Assert.True(cache.TryGet(K("/repoB", "a", "s3"), out _));
    }

    [Fact]
    public void Set_ExistingKey_ShouldReplaceValue_WithoutGrowing()
    {
        var cache = new BlameCache(capacity: 4);
        var key = K("/repo", "a", "s1");

        cache.Set(key, Val("old"));
        cache.Set(key, Val("new"));

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet(key, out var got));
        Assert.Equal("new", got[0].Sha);
    }

    [Fact]
    public void Capacity_ShouldNeverBeUnbounded_UnderManyInserts()
    {
        var cache = new BlameCache(capacity: 32);
        for (int i = 0; i < 500; i++)
        {
            cache.Set(K("/repo", "f" + i, "s" + i), Val("s" + i));
        }
        Assert.Equal(32, cache.Count);   // bounded — never grows past capacity
    }
}
