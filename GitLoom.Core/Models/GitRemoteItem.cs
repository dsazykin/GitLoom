namespace GitLoom.Core.Models;

/// <summary>
/// A configured Git remote (T-10). <see cref="PushUrl"/> is null when the push
/// URL is identical to <see cref="FetchUrl"/> (the common single-URL case), so a
/// non-null value signals a distinct <c>remote.&lt;name&gt;.pushurl</c>.
/// </summary>
public sealed class GitRemoteItem
{
    public string Name { get; init; } = "";
    public string FetchUrl { get; init; } = "";
    public string? PushUrl { get; init; }
}
