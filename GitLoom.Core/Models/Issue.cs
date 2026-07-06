using System;
using System.Collections.Generic;

namespace GitLoom.Core.Models;

/// <summary>Open/closed lifecycle state of a host issue (T-24), host-agnostic.</summary>
public enum IssueState { Open, Closed }

/// <summary>
/// One label on an issue (T-24). <see cref="Color"/> is the host's 6-digit hex (no leading <c>#</c>) —
/// data, not an app UI color — used to paint the chip background with an auto-contrast foreground.
/// </summary>
public sealed class IssueLabel
{
    public string Name { get; init; } = "";
    public string Color { get; init; } = ""; // host hex (6-digit), for the chip
}

/// <summary>
/// An issue as shown in the list (T-24). Host-agnostic projection produced by an
/// <c>IIssueProvider</c>; the ViewModel never sees a host's JSON shape (G-10).
/// </summary>
public sealed class IssueItem
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public IssueState State { get; init; }
    public int CommentCount { get; init; }
    public IReadOnlyList<IssueLabel> Labels { get; init; } = Array.Empty<IssueLabel>();
    public IReadOnlyList<string> Assignees { get; init; } = Array.Empty<string>();
    public string Url { get; init; } = "";            // web URL, for "open in browser"
    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A single comment on an issue (T-24).</summary>
public sealed class IssueComment
{
    public string Author { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTimeOffset When { get; init; }
}

/// <summary>Detailed view of a single issue (T-24): its summary, body, and comment thread.</summary>
public sealed class IssueDetail
{
    public IssueItem Summary { get; init; } = new();
    public string Body { get; init; } = "";
    public IReadOnlyList<IssueComment> Comments { get; init; } = Array.Empty<IssueComment>();
}

/// <summary>The fields needed to open an issue (T-24).</summary>
public sealed class CreateIssue
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Assignees { get; init; } = Array.Empty<string>();
}
