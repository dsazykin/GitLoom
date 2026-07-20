using System.Collections.Generic;
using System.Text.RegularExpressions;
using Mainguard.Git.Models;

namespace Mainguard.Git.Commits;

/// <summary>
/// Pure, host-agnostic parser (T-32) that extracts issue references from a pull-request title/body:
/// a bare <c>#123</c> (resolves to the PR's own repository) or a cross-repo <c>owner/repo#123</c>.
/// Closing keywords (<c>closes/fixes/resolves #n</c>) carry no extra signal here — the <c>#n</c> they
/// contain is captured like any other reference — so a closing-keyword mention and a plain mention of
/// the same issue collapse to one entry (dedup by repo + number). No IO / host / git types; unit-pinned.
/// </summary>
public static class IssueReferenceParser
{
    // A reference is [owner/repo]#<digits>, anchored so a bare "#n" is not picked up mid-identifier
    // (e.g. "color#fff" or "abc#7"): the lookbehind rejects a preceding word/slash/dot/dash char, and
    // the owner/repo prefix (two non-slash segments joined by "/") only matches the cross-repo form.
    private static readonly Regex Reference = new(
        @"(?<![A-Za-z0-9._/-])(?<repo>[A-Za-z0-9._-]+/[A-Za-z0-9._-]+)?#(?<num>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Extracts every issue reference from <paramref name="text"/>, in first-seen order, deduped by
    /// (repo, number). A bare <c>#n</c> is attributed to <paramref name="defaultRepoFullName"/> (the PR's
    /// own <c>owner/repo</c>); a <c>owner/repo#n</c> keeps its explicit repo.
    /// </summary>
    public static IReadOnlyList<LinkedIssueRef> Parse(string? text, string defaultRepoFullName = "")
    {
        var result = new List<LinkedIssueRef>();
        if (string.IsNullOrEmpty(text)) return result;

        var seen = new HashSet<(string Repo, int Number)>();
        foreach (Match m in Reference.Matches(text))
        {
            if (!int.TryParse(m.Groups["num"].Value, out var number)) continue;

            var repo = m.Groups["repo"].Success && m.Groups["repo"].Value.Length > 0
                ? m.Groups["repo"].Value
                : defaultRepoFullName ?? "";

            if (seen.Add((repo.ToLowerInvariant(), number)))
                result.Add(new LinkedIssueRef { Number = number, RepoFullName = repo });
        }

        return result;
    }
}
