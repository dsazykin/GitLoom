using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure parser for <c>git worktree list --porcelain</c> output (T-07). No repo/IO — unit-testable.
/// The format is line-oriented (one attribute per line, blank line between stanzas); paths are
/// <b>not</b> quoted, so the path is everything after <c>"worktree "</c> — never split on spaces.
/// </summary>
public static class WorktreePorcelainParser
{
    public static IReadOnlyList<WorktreeItem> Parse(string porcelain)
    {
        var result = new List<WorktreeItem>();
        if (string.IsNullOrEmpty(porcelain)) return result;

        string? path = null, headSha = null, branch = null;
        bool detached = false, locked = false;
        bool first = true;
        bool inStanza = false;

        void Flush()
        {
            if (!inStanza || path == null) return;
            result.Add(new WorktreeItem
            {
                Path = path,
                HeadSha = headSha,
                Branch = branch,
                IsDetached = detached,
                IsLocked = locked,
                IsMain = first
            });
            first = false;
            path = headSha = branch = null;
            detached = locked = false;
            inStanza = false;
        }

        foreach (var raw in porcelain.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw;
            if (line.Length == 0)
            {
                Flush(); // blank line ends a stanza
                continue;
            }

            if (line.StartsWith("worktree "))
            {
                Flush(); // a new "worktree" line starts a new stanza
                path = line.Substring("worktree ".Length);
                inStanza = true;
            }
            else if (line.StartsWith("HEAD "))
            {
                headSha = line.Substring("HEAD ".Length);
            }
            else if (line.StartsWith("branch "))
            {
                var reference = line.Substring("branch ".Length);
                const string prefix = "refs/heads/";
                branch = reference.StartsWith(prefix) ? reference.Substring(prefix.Length) : reference;
            }
            else if (line == "detached")
            {
                detached = true;
                branch = null;
            }
            else if (line == "locked" || line.StartsWith("locked "))
            {
                locked = true;
            }
            // Other attributes (e.g. "bare", "prunable") are ignored for v1.
        }

        Flush(); // final stanza may not be followed by a blank line
        return result;
    }
}
