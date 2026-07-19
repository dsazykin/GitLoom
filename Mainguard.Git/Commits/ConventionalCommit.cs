using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Mainguard.Git.Analytics;

namespace Mainguard.Git.Commits;

/// <summary>
/// A structured, editable conventional-commit draft (T-31): the <see cref="Type"/> (feat/fix/…),
/// optional <see cref="Scope"/>, the subject <see cref="Description"/>, an optional <see cref="Body"/>,
/// a <see cref="Breaking"/> flag with its <see cref="BreakingDescription"/>, and the
/// <see cref="CoAuthors"/> ("Name &lt;email&gt;") + <see cref="ClosesIssues"/> ("#12", "org/repo#7")
/// trailer lists. Pure data — <see cref="ConventionalCommitBuilder"/> assembles it into a message.
/// </summary>
public sealed class ConventionalCommitDraft
{
    public string Type { get; init; } = "";
    public string Scope { get; init; } = "";
    public string Description { get; init; } = "";
    public string Body { get; init; } = "";
    public bool Breaking { get; init; }
    public string BreakingDescription { get; init; } = "";
    public IReadOnlyList<string> CoAuthors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ClosesIssues { get; init; } = Array.Empty<string>();
}

/// <summary>
/// One commitlint-style validation finding (T-31): the <see cref="Field"/> it concerns, a human
/// <see cref="Message"/>, and whether it is a blocking <see cref="IsError"/> (vs an advisory warning).
/// Pure data.
/// </summary>
public sealed class CommitValidationIssue
{
    public string Field { get; init; } = "";
    public string Message { get; init; } = "";
    public bool IsError { get; init; }
}

/// <summary>
/// Pure, unit-pinned conventional-commit engine (T-31) — the inverse of T-28's
/// <see cref="ChangelogGenerator"/>. <see cref="Build"/> assembles a draft into a deterministic
/// <c>type(scope)!: description</c> message with a blank-line-separated body and a
/// <c>BREAKING CHANGE:</c> / <c>Closes …</c> / <c>Co-authored-by:</c> footer block;
/// <see cref="Validate"/> returns commitlint-style errors/warnings; <see cref="Parse"/> best-effort
/// recovers a draft from an existing message (reusing <see cref="ChangelogGenerator.ParseSubject"/>
/// for the header). No IO, no host/git types — the exact output is pinned in tests.
/// </summary>
public static class ConventionalCommitBuilder
{
    /// <summary>The standard conventional-commit type set (offered in the composer's Type dropdown).</summary>
    public static readonly IReadOnlyList<string> Types = new[]
    {
        "feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert",
    };

    /// <summary>The recommended max subject-line length; over this <see cref="Validate"/> warns.</summary>
    public const int SubjectSoftLimit = 72;

    // "Name <email>" — a non-empty name, then a single-address angle-bracket email. Used both to
    // filter co-authors out of Build and to flag a malformed one in Validate.
    private static readonly Regex CoAuthorPattern = new(
        @"^[^<>]+\s+<[^<>@\s]+@[^<>@\s]+>$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BreakingFooter = new(
        @"^BREAKING[ -]CHANGE:[ \t]*(?<desc>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CoAuthorFooter = new(
        @"^Co-authored-by:[ \t]*(?<val>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // A closing keyword followed by an issue-shaped ref ("#12", "org/repo#7", or a bare number) —
    // the ref shape guards a plain sentence like "Closes the door" from being read as a trailer.
    private static readonly Regex ClosesFooter = new(
        @"^(?:Closes?|Closed|Fix(?:e[sd])?|Resolve[sd]?)[ \t]+(?<ref>#\d+|[\w./-]+#\d+|\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Assembles the full commit message from a draft. Header is <c>type(scope)!: description</c>
    /// (no <c>()</c> when the scope is empty, a trailing <c>!</c> when breaking); an optional body and
    /// a footer block (<c>BREAKING CHANGE:</c>, one <c>Closes …</c> per issue ref, one
    /// <c>Co-authored-by:</c> per valid co-author) each follow a blank line. Deterministic; no trailing
    /// newline. Malformed co-authors are dropped (see <see cref="Validate"/>).
    /// </summary>
    public static string Build(ConventionalCommitDraft draft)
    {
        if (draft is null) return "";

        var sections = new List<string> { BuildHeader(draft) };

        var body = (draft.Body ?? "").Trim();
        if (body.Length > 0) sections.Add(body);

        var footers = new List<string>();
        if (draft.Breaking)
        {
            var bd = (draft.BreakingDescription ?? "").Trim();
            footers.Add(bd.Length == 0 ? "BREAKING CHANGE:" : $"BREAKING CHANGE: {bd}");
        }
        foreach (var issue in draft.ClosesIssues ?? Array.Empty<string>())
        {
            var reference = NormalizeIssueRef(issue);
            if (reference.Length > 0) footers.Add($"Closes {reference}");
        }
        foreach (var coAuthor in draft.CoAuthors ?? Array.Empty<string>())
        {
            var c = (coAuthor ?? "").Trim();
            if (CoAuthorPattern.IsMatch(c)) footers.Add($"Co-authored-by: {c}");
        }
        if (footers.Count > 0) sections.Add(string.Join("\n", footers));

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// commitlint-style checks. Errors (block the default Commit): missing/unknown type, empty
    /// description, a malformed co-author. Warnings (advisory): a subject over
    /// <see cref="SubjectSoftLimit"/> chars, breaking-without-a-description, a trailing period, and a
    /// non-imperative-looking first word. Order is stable.
    /// </summary>
    public static IReadOnlyList<CommitValidationIssue> Validate(ConventionalCommitDraft draft)
    {
        var issues = new List<CommitValidationIssue>();
        if (draft is null) return issues;

        var type = (draft.Type ?? "").Trim();
        var description = (draft.Description ?? "").Trim();

        if (type.Length == 0)
            issues.Add(Error("Type", "Select a commit type."));
        else if (!Types.Contains(type.ToLowerInvariant()))
            issues.Add(Error("Type", $"\"{type}\" is not a conventional-commit type."));

        if (description.Length == 0)
            issues.Add(Error("Description", "Describe the change in the subject."));

        var header = BuildHeader(draft);
        if (header.Length > SubjectSoftLimit)
            issues.Add(Warn("Description", $"Subject line is {header.Length} characters — keep it under {SubjectSoftLimit}."));

        if (draft.Breaking && string.IsNullOrWhiteSpace(draft.BreakingDescription))
            issues.Add(Warn("BreakingDescription", "Describe what breaks and how to migrate."));

        foreach (var coAuthor in draft.CoAuthors ?? Array.Empty<string>())
        {
            var c = (coAuthor ?? "").Trim();
            if (c.Length > 0 && !CoAuthorPattern.IsMatch(c))
                issues.Add(Error("CoAuthors", $"Co-author must be \"Name <email>\": {c}"));
        }

        if (description.Length > 0)
        {
            if (description.EndsWith(".", StringComparison.Ordinal))
                issues.Add(Warn("Description", "Subject should not end with a period."));

            var firstWord = description.Split(' ', 2)[0].ToLowerInvariant();
            if (firstWord.Length > 3
                && (firstWord.EndsWith("ed", StringComparison.Ordinal) || firstWord.EndsWith("ing", StringComparison.Ordinal)))
                issues.Add(Warn("Description", "Use the imperative mood (e.g. \"add\", not \"added\")."));
        }

        return issues;
    }

    /// <summary>
    /// Best-effort parse of an existing message back into a draft. The first line goes through
    /// <see cref="ChangelogGenerator.ParseSubject"/> (a non-conventional subject yields an empty type,
    /// carrying the whole line as the description); <c>BREAKING CHANGE:</c> / <c>Closes …</c> /
    /// <c>Co-authored-by:</c> lines are lifted into their fields and everything else is the body.
    /// <c>Parse(Build(draft))</c> recovers the stable fields.
    /// </summary>
    public static ConventionalCommitDraft Parse(string message)
    {
        var text = (message ?? "").Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = text.Split('\n');
        var subject = lines.Length > 0 ? lines[0] : "";

        var entry = ChangelogGenerator.ParseSubject("", subject);
        var type = entry.Type == "other" ? "" : entry.Type;

        var breaking = entry.Breaking;
        var breakingDescription = "";
        var coAuthors = new List<string>();
        var closes = new List<string>();
        var bodyLines = new List<string>();
        var bodyStarted = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            var bm = BreakingFooter.Match(trimmed);
            if (bm.Success)
            {
                breaking = true;
                breakingDescription = bm.Groups["desc"].Value.Trim();
                continue;
            }

            var cm = CoAuthorFooter.Match(trimmed);
            if (cm.Success)
            {
                coAuthors.Add(cm.Groups["val"].Value.Trim());
                continue;
            }

            var im = ClosesFooter.Match(trimmed);
            if (im.Success)
            {
                closes.Add(im.Groups["ref"].Value.Trim());
                continue;
            }

            if (trimmed.Length == 0)
            {
                if (bodyStarted) bodyLines.Add("");
                continue;
            }

            bodyStarted = true;
            bodyLines.Add(line);
        }

        return new ConventionalCommitDraft
        {
            Type = type,
            Scope = entry.Scope,
            Description = entry.Description,
            Body = string.Join("\n", bodyLines).Trim(),
            Breaking = breaking,
            BreakingDescription = breakingDescription,
            CoAuthors = coAuthors,
            ClosesIssues = closes,
        };
    }

    /// <summary>Builds just the subject line (<c>type(scope)!: description</c>), trailing space trimmed.</summary>
    public static string BuildHeader(ConventionalCommitDraft draft)
    {
        if (draft is null) return "";
        var type = (draft.Type ?? "").Trim();
        var scope = (draft.Scope ?? "").Trim();
        var description = (draft.Description ?? "").Trim();

        var scopePart = scope.Length == 0 ? "" : $"({scope})";
        var bang = draft.Breaking ? "!" : "";
        return $"{type}{scopePart}{bang}: {description}".TrimEnd();
    }

    // "#12"/"org/repo#7" pass through; a bare number gets a leading "#"; anything else is verbatim.
    private static string NormalizeIssueRef(string? issue)
    {
        var s = (issue ?? "").Trim();
        if (s.Length == 0) return "";
        if (s.Contains('#')) return s;
        if (s.All(char.IsDigit)) return "#" + s;
        return s;
    }

    private static CommitValidationIssue Error(string field, string message)
        => new() { Field = field, Message = message, IsError = true };

    private static CommitValidationIssue Warn(string field, string message)
        => new() { Field = field, Message = message, IsError = false };
}
