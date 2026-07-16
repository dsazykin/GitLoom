using System.Collections.Generic;

namespace GitLoom.Core.Models;

/// <summary>
/// One node in a slash-delimited branch-name tree (issue #71): either a folder (a shared path
/// segment, e.g. <c>feature</c>) with <see cref="Children"/>, or a leaf naming one actual branch
/// (<see cref="FullName"/> is the branch's full friendly name, e.g. <c>feature/foo</c>). Plain
/// data; no repo/IO/UI.
/// </summary>
public sealed class BranchTreeNode
{
    /// <summary>The path segment shown for this node (e.g. <c>feature</c> for a folder, or the
    /// last segment of the branch name for a leaf, e.g. <c>foo</c> for <c>feature/foo</c>).</summary>
    public string Name { get; init; } = "";

    /// <summary>The branch's full friendly name (e.g. <c>feature/foo</c>). Null for a folder node.</summary>
    public string? FullName { get; init; }

    public bool IsFolder { get; init; }

    public List<BranchTreeNode> Children { get; } = new();
}
