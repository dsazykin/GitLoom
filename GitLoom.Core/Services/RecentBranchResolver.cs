using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure helper (issue #70) that derives "recently checked out" branch ordering from HEAD's reflog
/// instead of alphabetical sorting. Git's own reflog records a checkout as a message of the form
/// <c>"checkout: moving from &lt;old&gt; to &lt;new&gt;"</c> (see <c>git help reflog</c>); this walks the
/// already most-recent-first <see cref="ReflogItem"/> list (from <c>IGitService.GetReflog</c>), extracts
/// the checkout target from each matching entry, and keeps the first distinct targets that still name a
/// branch that exists today (a reflog entry may reference a since-deleted branch, or a detached-HEAD
/// checkout to a raw SHA — both are skipped rather than guessed at). No repo/IO.
/// </summary>
public static class RecentBranchResolver
{
    // "checkout: moving from <old> to <new>" — only the target (group 1) is a recency signal;
    // the source is irrelevant here. Anchored so a reflog message from a different kind of
    // mutation (commit/reset/merge/…) never matches.
    private static readonly Regex CheckoutTargetPattern =
        new(@"^checkout:\s*moving from\s+.+\s+to\s+(?<target>.+)$", RegexOptions.Compiled);

    /// <summary>
    /// Returns up to <paramref name="take"/> branch names ordered by actual checkout recency.
    /// Reflog-derived hits come first (newest checkout first, deduplicated, filtered to branches
    /// present in <paramref name="existingLocalBranches"/>); if the reflog doesn't yield enough
    /// distinct, still-existing branches, the remaining slots are filled from
    /// <paramref name="fallbackOrder"/> (e.g. an alphabetical list) so the result still has content
    /// on a fresh/shallow reflog rather than being sparse.
    /// </summary>
    public static IReadOnlyList<string> Resolve(
        IEnumerable<ReflogItem> reflogNewestFirst,
        IReadOnlyCollection<string> existingLocalBranches,
        IEnumerable<string> fallbackOrder,
        int take = 3)
    {
        if (take <= 0) return Array.Empty<string>();

        var existing = new HashSet<string>(existingLocalBranches, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(take);

        foreach (var entry in reflogNewestFirst)
        {
            if (result.Count >= take) break;

            var message = entry.Message ?? string.Empty;
            var match = CheckoutTargetPattern.Match(message);
            if (!match.Success) continue;

            var target = match.Groups["target"].Value.Trim();
            if (target.Length == 0) continue;
            if (!existing.Contains(target)) continue; // deleted branch or a raw SHA (detached HEAD)
            if (!seen.Add(target)) continue; // already have this one, newer entry wins

            result.Add(target);
        }

        if (result.Count < take)
        {
            foreach (var name in fallbackOrder)
            {
                if (result.Count >= take) break;
                if (seen.Add(name)) result.Add(name);
            }
        }

        return result;
    }
}
