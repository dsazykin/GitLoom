using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitLoom.Core.Analytics;

/// <summary>
/// One parsed commit for the changelog (T-28): a conventional-commit <see cref="Type"/> (feat/fix/…,
/// or <c>other</c> for a non-conventional subject), optional <see cref="Scope"/>, human
/// <see cref="Description"/>, the short <see cref="Sha"/>, and whether it is a <see cref="Breaking"/>
/// change (<c>!</c> after the type or a <c>BREAKING CHANGE</c> marker). Pure data.
/// </summary>
public sealed class ChangelogEntry
{
    public string Type { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Description { get; init; } = "";
    public string Sha { get; init; } = "";
    public bool Breaking { get; init; }
}

/// <summary>
/// Pure, unit-pinned changelog builder (T-28) — the offline heart of the Releases feature and the seam an
/// agent later reuses to draft notes. <see cref="ParseSubject"/> turns a single commit subject into a
/// <see cref="ChangelogEntry"/>; <see cref="BuildNotes"/> groups entries into grouped markdown
/// (Breaking Changes / Features / Fixes / Other) with a compact <c>- desc (sha7)</c> list and a
/// "Full changelog" range line. No IO, no host or git types — the exact output is pinned in tests.
/// </summary>
public static class ChangelogGenerator
{
    // "type(scope)!: description" — type is letters, scope is anything but ')', an optional '!' marks a
    // breaking change, then a colon. A subject that doesn't match is a plain (non-conventional) subject.
    private static readonly Regex ConventionalSubject = new(
        @"^(?<type>[a-zA-Z]+)(\((?<scope>[^)]*)\))?(?<bang>!)?:[ \t]*(?<desc>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses a conventional-commit subject into an entry. <c>feat(scope)!: desc</c> / <c>fix: desc</c>
    /// yield their type/scope/description; a non-conventional subject becomes <c>Type="other"</c> carrying
    /// the whole subject as the description (never dropped). Breaking is set by a <c>!</c> after the type
    /// or a <c>BREAKING CHANGE</c> / <c>BREAKING-CHANGE</c> marker anywhere in the subject.
    /// </summary>
    public static ChangelogEntry ParseSubject(string sha, string subject)
    {
        var shortSha = ShortSha(sha);
        var text = (subject ?? "").Trim();
        var markerBreaking = text.Contains("BREAKING CHANGE", StringComparison.Ordinal)
                             || text.Contains("BREAKING-CHANGE", StringComparison.Ordinal);

        var m = ConventionalSubject.Match(text);
        if (m.Success)
        {
            return new ChangelogEntry
            {
                Type = m.Groups["type"].Value.ToLowerInvariant(),
                Scope = m.Groups["scope"].Value.Trim(),
                Description = m.Groups["desc"].Value.Trim(),
                Sha = shortSha,
                Breaking = m.Groups["bang"].Success || markerBreaking,
            };
        }

        return new ChangelogEntry
        {
            Type = "other",
            Scope = "",
            Description = text,
            Sha = shortSha,
            Breaking = markerBreaking,
        };
    }

    /// <summary>
    /// Groups entries into markdown notes. Breaking entries are called out first (under Breaking Changes,
    /// only there — never double-listed); the remaining entries fall under Features (<c>feat</c>) /
    /// Fixes (<c>fix</c>) / Other (everything else, incl. <c>other</c>). Each line is
    /// <c>- [**scope:** ]description (sha7)</c>. A "Full changelog" range line closes the notes. An empty
    /// entry set yields an empty string (no headers, no throw).
    /// </summary>
    public static string BuildNotes(IEnumerable<ChangelogEntry> entries, string? previousTag, string newTag)
    {
        var all = (entries ?? Enumerable.Empty<ChangelogEntry>()).ToList();
        if (all.Count == 0) return "";

        var breaking = all.Where(e => e.Breaking).ToList();
        var nonBreaking = all.Where(e => !e.Breaking).ToList();
        var features = nonBreaking.Where(e => e.Type == "feat").ToList();
        var fixes = nonBreaking.Where(e => e.Type == "fix").ToList();
        var other = nonBreaking.Where(e => e.Type != "feat" && e.Type != "fix").ToList();

        var blocks = new List<string>();
        AddSection(blocks, "Breaking Changes", breaking);
        AddSection(blocks, "Features", features);
        AddSection(blocks, "Fixes", fixes);
        AddSection(blocks, "Other", other);

        var range = string.IsNullOrEmpty(previousTag) ? newTag : $"{previousTag}...{newTag}";
        blocks.Add($"**Full changelog:** {range}");

        return string.Join("\n\n", blocks);
    }

    private static void AddSection(List<string> blocks, string title, IReadOnlyList<ChangelogEntry> items)
    {
        if (items.Count == 0) return;
        var lines = new List<string> { $"### {title}" };
        lines.AddRange(items.Select(FormatEntry));
        blocks.Add(string.Join("\n", lines));
    }

    private static string FormatEntry(ChangelogEntry e)
    {
        var scope = string.IsNullOrEmpty(e.Scope) ? "" : $"**{e.Scope}:** ";
        var desc = string.IsNullOrEmpty(e.Description) ? "(no description)" : e.Description;
        return $"- {scope}{desc} ({e.Sha})";
    }

    private static string ShortSha(string sha)
    {
        var s = (sha ?? "").Trim();
        return s.Length <= 7 ? s : s.Substring(0, 7);
    }
}
