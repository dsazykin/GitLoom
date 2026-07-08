using System;
using System.Collections.Generic;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

// Issue #70: "Recent" branches must come from actual checkout recency (HEAD reflog), not an
// alphabetical slice of the local branch list.
public class RecentBranchResolverTests
{
    private static ReflogItem Checkout(string from, string to) => new()
    {
        FromSha = "aaa",
        ToSha = "bbb",
        Message = $"checkout: moving from {from} to {to}",
        When = DateTimeOffset.UtcNow
    };

    private static ReflogItem NonCheckout(string message) => new()
    {
        FromSha = "aaa",
        ToSha = "bbb",
        Message = message,
        When = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Resolve_NewestFirstCheckouts_ShouldOrderByRecency_NotAlphabetically()
    {
        // Newest-first reflog: most recently checked out is "zeta", then "alpha", then "main".
        var reflog = new[]
        {
            Checkout("alpha", "zeta"),
            Checkout("main", "alpha"),
            Checkout("zeta", "main"),
        };
        var existing = new[] { "alpha", "main", "zeta" };

        var result = RecentBranchResolver.Resolve(reflog, existing, fallbackOrder: Array.Empty<string>());

        Assert.Equal(new[] { "zeta", "alpha", "main" }, result);
    }

    [Fact]
    public void Resolve_DuplicateCheckouts_ShouldKeepOnlyNewestOccurrence()
    {
        var reflog = new[]
        {
            Checkout("main", "feature"),
            Checkout("feature", "main"),
            Checkout("main", "feature"), // older re-visit of "feature" — already captured above
        };
        var existing = new[] { "main", "feature" };

        var result = RecentBranchResolver.Resolve(reflog, existing, fallbackOrder: Array.Empty<string>());

        Assert.Equal(new[] { "feature", "main" }, result);
    }

    [Fact]
    public void Resolve_TargetNoLongerExists_ShouldSkipEntry()
    {
        // "deleted-branch" was checked out most recently but no longer exists (branch was deleted).
        var reflog = new[]
        {
            Checkout("main", "deleted-branch"),
            Checkout("feature", "main"),
        };
        var existing = new[] { "main", "feature" };

        var result = RecentBranchResolver.Resolve(reflog, existing, fallbackOrder: Array.Empty<string>());

        Assert.Equal(new[] { "main" }, result);
    }

    [Fact]
    public void Resolve_DetachedHeadCheckoutToRawSha_ShouldSkipEntry()
    {
        var reflog = new[]
        {
            Checkout("main", "a1b2c3d4e5f6"), // detached HEAD onto a commit sha, not a branch
            Checkout("feature", "main"),
        };
        var existing = new[] { "main", "feature" };

        var result = RecentBranchResolver.Resolve(reflog, existing, fallbackOrder: Array.Empty<string>());

        Assert.Equal(new[] { "main" }, result);
    }

    [Fact]
    public void Resolve_NonCheckoutEntries_ShouldBeIgnored()
    {
        var reflog = new[]
        {
            NonCheckout("commit: fix bug"),
            NonCheckout("reset: moving to HEAD~1"),
            Checkout("main", "feature"),
        };
        var existing = new[] { "main", "feature" };

        var result = RecentBranchResolver.Resolve(reflog, existing, fallbackOrder: Array.Empty<string>());

        Assert.Equal(new[] { "feature" }, result);
    }

    [Fact]
    public void Resolve_FewerThanTakeFromReflog_ShouldFillRemainderFromFallback()
    {
        var reflog = new[] { Checkout("main", "feature") };
        var existing = new[] { "main", "feature", "alpha", "beta" };
        var fallback = new[] { "alpha", "beta", "feature", "main" }; // e.g. alphabetical

        var result = RecentBranchResolver.Resolve(reflog, existing, fallback, take: 3);

        Assert.Equal(new[] { "feature", "alpha", "beta" }, result);
    }

    [Fact]
    public void Resolve_EmptyReflog_ShouldReturnFallbackOnly()
    {
        var result = RecentBranchResolver.Resolve(
            Array.Empty<ReflogItem>(),
            existingLocalBranches: new[] { "main", "alpha", "beta" },
            fallbackOrder: new[] { "alpha", "beta", "main" },
            take: 3);

        Assert.Equal(new[] { "alpha", "beta", "main" }, result);
    }

    [Fact]
    public void Resolve_RespectsTakeLimit()
    {
        var reflog = new[]
        {
            Checkout("d", "a"),
            Checkout("c", "d"),
            Checkout("b", "c"),
            Checkout("a", "b"),
        };
        var existing = new[] { "a", "b", "c", "d" };

        var result = RecentBranchResolver.Resolve(reflog, existing, Array.Empty<string>(), take: 2);

        Assert.Equal(new[] { "a", "d" }, result);
    }
}
