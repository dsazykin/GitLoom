using System;

namespace Mainguard.Git.Models;

/// <summary>
/// One revision of a file in its history (T-12): the commit that touched the file plus the
/// file's path <em>at that commit</em> (<see cref="PathAtCommit"/> follows renames, so the same
/// logical file can appear under different names down its history). Pure data — the file-history
/// view binds directly to a newest-first list of these.
/// </summary>
public sealed class FileVersion
{
    public string Sha { get; init; } = "";

    /// <summary>Abbreviated SHA for display (mirrors <see cref="GitCommitItem.ShortSha"/>).</summary>
    public string ShortSha => Sha.Length >= 7 ? Sha.Substring(0, 7) : Sha;

    public string PathAtCommit { get; init; } = "";
    public string MessageShort { get; init; } = "";
    public DateTimeOffset When { get; init; } = default;
    public string AuthorName { get; init; } = "";
}
