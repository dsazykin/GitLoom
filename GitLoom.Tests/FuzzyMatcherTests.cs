using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Actions;
using Xunit;

namespace GitLoom.Tests;

// TI-18: the fuzzy matcher is the ranked heart of the palette, so its scoring is PINNED here — both the
// relative ranking (the "chb" table) and exact scores for a fixed candidate set — plus every edge the
// Master Doc calls out (empty query, non-subsequence, case, word-boundary + consecutive bonuses).
public class FuzzyMatcherTests
{
    [Fact]
    public void Rank_chb_ShouldRankCheckoutBranch_AboveCherryPickBranchB()
    {
        var candidates = new[] { "Cherry-pick branch b", "Checkout Branch", "Rebase" };

        var ranked = FuzzyMatcher.Rank("chb", candidates, c => c);

        Assert.Equal("Checkout Branch", ranked[0].Item);
        Assert.Equal("Cherry-pick branch b", ranked[1].Item);
        Assert.Equal(2, ranked.Count); // "Rebase" is not a subsequence of "chb" and drops out
        Assert.True(ranked[0].Score > ranked[1].Score);
    }

    // Exact pinned scores for the ranking table — a change to the scoring weights must update these on purpose.
    [Theory]
    [InlineData("chb", "Checkout Branch", 73)]
    [InlineData("chb", "Cherry-pick branch b", 70)]
    public void Score_ShouldMatchPinnedValues(string query, string candidate, int expected)
    {
        Assert.Equal(expected, FuzzyMatcher.Score(query, candidate));
    }

    [Fact]
    public void Score_WordBoundaryMatch_ShouldBeatMidWordMatch()
    {
        // 'b' at the start of the word "Bar" (boundary) should score higher than 'b' buried mid-word.
        int boundary = FuzzyMatcher.Score("b", "Foo Bar");   // matches the 'B' in "Bar"
        int midWord = FuzzyMatcher.Score("b", "abcd");        // matches the 'b' at index 1, mid-word

        Assert.True(boundary > midWord, $"boundary={boundary} midWord={midWord}");
    }

    [Fact]
    public void Score_ConsecutiveRun_ShouldBeatScatteredMatch()
    {
        // "ab" consecutive ("abxx") vs the same chars scattered ("axbx").
        int consecutive = FuzzyMatcher.Score("ab", "abxx");
        int scattered = FuzzyMatcher.Score("ab", "axbx");

        Assert.True(consecutive > scattered, $"consecutive={consecutive} scattered={scattered}");
    }

    [Fact]
    public void Score_NonSubsequence_ShouldReturnNoMatch()
    {
        Assert.Equal(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("xyz", "Checkout Branch"));
        Assert.Equal(FuzzyMatcher.NoMatch, FuzzyMatcher.Score("ba", "abc")); // order matters
    }

    [Fact]
    public void Match_ShouldBeCaseInsensitive()
    {
        var upper = FuzzyMatcher.Match("CB", "Checkout Branch");
        var lower = FuzzyMatcher.Match("cb", "Checkout Branch");

        Assert.True(upper.IsMatch);
        Assert.True(lower.IsMatch);
        Assert.Equal(lower.Score, upper.Score);
        Assert.Equal(lower.Positions, upper.Positions);
    }

    [Fact]
    public void Match_ShouldReportMatchedPositionsForHighlighting()
    {
        var m = FuzzyMatcher.Match("chb", "Checkout Branch");

        Assert.True(m.IsMatch);
        Assert.Equal(new[] { 0, 1, 9 }, m.Positions); // C, h, and the 'B' of "Branch"
    }

    [Fact]
    public void EmptyQuery_ShouldMatchEverything_WithScoreZero()
    {
        var m = FuzzyMatcher.Match("", "anything");
        Assert.True(m.IsMatch);
        Assert.Equal(0, m.Score);
        Assert.Empty(m.Positions);

        var candidates = new[] { "One", "Two", "Three" };
        var ranked = FuzzyMatcher.Rank("", candidates, c => c);
        Assert.Equal(3, ranked.Count);
        Assert.All(ranked, r => Assert.Equal(0, r.Score));
        // Empty query preserves input order (stable).
        Assert.Equal(new[] { "One", "Two", "Three" }, ranked.Select(r => r.Item).ToArray());
    }

    [Fact]
    public void Score_RealSubsequenceMatch_ShouldNeverBeNegative()
    {
        // A far-apart-but-valid subsequence must stay >= 0 so callers can treat "< 0" as no-match.
        int score = FuzzyMatcher.Score("az", "a" + new string('x', 100) + "z");
        Assert.True(score >= 0);
    }

    [Fact]
    public void Rank_ShouldBeDeterministic_TieBreakingByLengthThenOrdinal()
    {
        // Two candidates that score identically for "a" differ only by length / ordinal.
        var candidates = new[] { "alpha", "ab", "aa" };
        var ranked = FuzzyMatcher.Rank("a", candidates, c => c);
        // "aa" and "ab" both length 2; ordinal puts "aa" before "ab"; "alpha" (len 5) last.
        Assert.Equal(new[] { "aa", "ab", "alpha" }, ranked.Select(r => r.Item).ToArray());
    }
}
