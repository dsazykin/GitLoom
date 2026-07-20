using System;

namespace Mainguard.Git.Models;

/// <summary>
/// One reflog entry for a ref (T-20): a single move of where the ref pointed, oldest→newest read
/// from Git's own reflog (<c>repo.Refs.Log(refName)</c>). <see cref="FromSha"/> is the position the
/// ref moved <em>from</em> (all-zero SHA for the very first entry / branch creation), <see cref="ToSha"/>
/// is where it moved <em>to</em>. <see cref="Message"/> is the reflog note (e.g. "commit: …",
/// "reset: moving to …", "checkout: moving from … to …") — only its first line is kept so a
/// multi-line message stays a single row. Plain data; no repo/IO.
/// </summary>
public sealed class ReflogItem
{
    public string FromSha { get; init; } = "";
    public string ToSha { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTimeOffset When { get; init; }
}
