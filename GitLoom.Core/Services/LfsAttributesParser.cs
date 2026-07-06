using System;
using System.Collections.Generic;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure parser for the LFS-tracked patterns in a <c>.gitattributes</c> file (T-17). A pattern is
/// LFS-tracked when its attribute line routes the pattern through the LFS filter (<c>filter=lfs</c>);
/// the pattern is the first whitespace-delimited token. Comments (<c>#</c>) and blank lines are
/// skipped. git-lfs encodes spaces inside a pattern as <c>[[:space:]]</c>, which is decoded back to a
/// space for display. No IO — unit-testable.
/// </summary>
public static class LfsAttributesParser
{
    public static IReadOnlyList<string> Parse(string? gitattributes)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(gitattributes)) return result;

        foreach (var raw in gitattributes.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line.IndexOf("filter=lfs", StringComparison.Ordinal) < 0) continue;

            var sp = line.IndexOfAny(new[] { ' ', '\t' });
            var pattern = (sp < 0 ? line : line.Substring(0, sp)).Replace("[[:space:]]", " ");
            if (pattern.Length > 0) result.Add(pattern);
        }
        return result;
    }
}
