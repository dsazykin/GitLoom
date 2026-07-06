namespace GitLoom.Core.Models;

/// <summary>
/// One entry from <c>git lfs ls-files</c> (T-17): the LFS object's OID, the working-tree path it is
/// tracked at, and whether the real content is present locally (checked out) or the working tree
/// still holds only the pointer. A plain immutable data type produced by the pure
/// <see cref="GitLoom.Core.Services.LfsLsFilesParser"/>.
/// </summary>
public sealed class LfsFile
{
    /// <summary>The object's LFS OID (sha256); short (10-char) or full (64-char) per the ls-files mode.</summary>
    public string Oid { get; init; } = "";

    /// <summary>Working-tree path the object is tracked at (may contain spaces).</summary>
    public string Path { get; init; } = "";

    /// <summary>True when the real content is present locally (<c>*</c>); false when only the pointer is (<c>-</c>).</summary>
    public bool IsDownloaded { get; init; }
}
