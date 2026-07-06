using System;
using System.Collections.Generic;
using System.Linq;

namespace GitLoom.Core.Actions;

/// <summary>
/// A pure, deterministic subsequence fuzzy matcher (T-18). A query matches a candidate iff its characters
/// appear in order (case-insensitive) somewhere in the candidate. Scoring rewards matches that land on
/// word boundaries and in consecutive runs, and penalises gaps between matched characters, so the palette
/// ranking is stable and unit-pinnable. The greedy left-to-right match is intentionally simple and
/// deterministic (no backtracking): good enough for short command titles and cheap to reason about.
/// </summary>
public static class FuzzyMatcher
{
    // Scoring weights. These are pinned by FuzzyMatcherTests — changing them is a public-contract change.
    internal const int MatchBonus = 10;         // every matched character
    internal const int FirstCharBonus = 10;     // the very first character of the candidate
    internal const int WordBoundaryBonus = 15;  // match at the start of a word
    internal const int ConsecutiveBonus = 10;   // match immediately after the previous match
    internal const int GapPenaltyPerChar = 1;   // per character skipped between two matches (capped)
    internal const int MaxGapPenalty = 20;      // clamp so a real subsequence match never goes negative

    /// <summary>Sentinel returned by <see cref="Score"/> when the query is not a subsequence of the candidate.</summary>
    public const int NoMatch = int.MinValue;

    /// <summary>The outcome of matching a query against one candidate: whether it matched, its score, and the
    /// 0-based indices of the matched characters in the candidate (for highlighting).</summary>
    public readonly record struct MatchResult(bool IsMatch, int Score, IReadOnlyList<int> Positions);

    /// <summary>
    /// Matches <paramref name="query"/> against <paramref name="candidate"/>. An empty/whitespace query
    /// matches everything with score 0 and no highlighted positions.
    /// </summary>
    public static MatchResult Match(string? query, string? candidate)
    {
        candidate ??= string.Empty;
        if (string.IsNullOrEmpty(query))
            return new MatchResult(true, 0, Array.Empty<int>());

        var positions = new List<int>(query.Length);
        int ci = 0;
        for (int qi = 0; qi < query.Length; qi++)
        {
            char qc = char.ToLowerInvariant(query[qi]);
            bool found = false;
            for (; ci < candidate.Length; ci++)
            {
                if (char.ToLowerInvariant(candidate[ci]) == qc)
                {
                    positions.Add(ci);
                    ci++;
                    found = true;
                    break;
                }
            }
            if (!found)
                return new MatchResult(false, NoMatch, Array.Empty<int>());
        }

        int score = ScorePositions(candidate, positions);
        return new MatchResult(true, score, positions);
    }

    /// <summary>Convenience: the score only. Returns <see cref="NoMatch"/> for a non-subsequence.</summary>
    public static int Score(string? query, string? candidate) => Match(query, candidate).Score;

    /// <summary>
    /// Ranks <paramref name="items"/> by their <paramref name="text"/> against <paramref name="query"/>,
    /// dropping non-matches. Ordering: score descending, then shorter candidate, then ordinal text, then a
    /// stable input-order fallback — fully deterministic. An empty query returns every item (score 0) in
    /// input order.
    /// </summary>
    public static IReadOnlyList<(T Item, int Score)> Rank<T>(string? query, IEnumerable<T> items, Func<T, string> text)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (text is null) throw new ArgumentNullException(nameof(text));

        var scored = new List<(T Item, int Score, string Text, int Index)>();
        int index = 0;
        foreach (var item in items)
        {
            var t = text(item) ?? string.Empty;
            var m = Match(query, t);
            if (m.IsMatch)
                scored.Add((item, m.Score, t, index));
            index++;
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Text.Length)
            .ThenBy(s => s.Text, StringComparer.Ordinal)
            .ThenBy(s => s.Index)
            .Select(s => (s.Item, s.Score))
            .ToList();
    }

    private static int ScorePositions(string candidate, IReadOnlyList<int> positions)
    {
        int score = 0;
        int prev = -1;
        foreach (int pos in positions)
        {
            score += MatchBonus;
            if (pos == 0)
                score += FirstCharBonus;
            if (IsWordBoundary(candidate, pos))
                score += WordBoundaryBonus;
            if (prev >= 0)
            {
                if (pos == prev + 1)
                    score += ConsecutiveBonus;
                else
                {
                    int skipped = pos - prev - 1;
                    score -= Math.Min(skipped, MaxGapPenalty) * GapPenaltyPerChar;
                }
            }
            prev = pos;
        }
        // A genuine subsequence match is always non-negative (the sentinel is the only negative value).
        return Math.Max(score, 0);
    }

    private static bool IsWordBoundary(string candidate, int index)
    {
        if (index <= 0) return true;
        char prev = candidate[index - 1];
        char cur = candidate[index];
        if (!char.IsLetterOrDigit(prev)) return true;                 // preceded by space, '-', '_', '/', '.', …
        if (char.IsLower(prev) && char.IsUpper(cur)) return true;     // camelCase boundary
        return false;
    }
}
