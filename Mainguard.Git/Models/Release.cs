using System;

namespace Mainguard.Git.Models;

/// <summary>
/// A published (or draft) release as shown in the Releases panel (T-28). Host-agnostic projection produced
/// by an <c>IReleaseProvider</c>; the ViewModel never sees a host's JSON shape (G-10). A release is a tag
/// plus its notes/metadata — the tag itself is data the T-05 tag reads already surface.
/// </summary>
public sealed class ReleaseItem
{
    public long Id { get; init; }
    public string TagName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
    public string Author { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public string Url { get; init; } = "";   // web URL, for "open in browser"
}

/// <summary>
/// The fields needed to cut a release (T-28). <see cref="TargetCommitish"/> is only needed when
/// <see cref="TagName"/> names a tag that does not exist yet (the host creates the tag at that
/// branch/sha); for an existing tag it is ignored.
/// </summary>
public sealed class CreateRelease
{
    public string TagName { get; init; } = "";
    public string TargetCommitish { get; init; } = "";   // branch or sha the new tag points at
    public string Name { get; init; } = "";
    public string Body { get; init; } = "";
    public bool IsDraft { get; init; }
    public bool IsPrerelease { get; init; }
}
