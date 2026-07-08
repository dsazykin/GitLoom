using System;
using System.Collections.Generic;
using System.Linq;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure tree-grouping helper (issue #71): turns a flat list of slash-delimited branch friendly
/// names (e.g. <c>feature/foo</c>, <c>feature/bar</c>, <c>main</c>) into a nested
/// <see cref="BranchTreeNode"/> forest — a top-level name with no <c>/</c> is a top-level leaf;
/// names sharing a <c>prefix/</c> are grouped under a shared folder node for that segment, applied
/// recursively for any deeper shared segments (standard slash-delimited tree, like most git GUIs'
/// branch panes). No repo/IO — strings in, tree out.
/// </summary>
public static class BranchTreeBuilder
{
    public static IReadOnlyList<BranchTreeNode> Build(IEnumerable<string> friendlyNames)
    {
        var roots = new List<BranchTreeNode>();

        foreach (var name in friendlyNames)
        {
            if (string.IsNullOrEmpty(name)) continue;

            var parts = name.Split('/');
            var siblings = roots;

            // Walk/create the intermediate folder nodes for every segment but the last.
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var segment = parts[i];
                var folder = siblings.FirstOrDefault(n => n.IsFolder && n.Name == segment);
                if (folder == null)
                {
                    folder = new BranchTreeNode { Name = segment, IsFolder = true };
                    siblings.Add(folder);
                }
                siblings = folder.Children;
            }

            // The final segment is the leaf naming the actual branch.
            siblings.Add(new BranchTreeNode { Name = parts[^1], FullName = name, IsFolder = false });
        }

        return roots;
    }
}
