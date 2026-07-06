using System;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure detection of a Git LFS pointer blob (T-17). An LFS pointer is a small text file whose first
/// line is exactly the spec version line; the diff viewer uses this to render "LFS object (size)"
/// instead of the raw pointer text. No IO — unit-testable.
/// </summary>
public static class LfsPointer
{
    // The canonical first line of every v1 LFS pointer. Detection is anchored to this line only.
    private const string VersionLine = "version https://git-lfs.github.com/spec/v1";

    /// <summary>
    /// True iff <paramref name="content"/> is a Git LFS pointer — i.e. its <b>first</b> line is the
    /// LFS spec version line. Malformed/partial variants (a leading blank line, a wrong URL/version,
    /// null/empty) return false. Tolerates a trailing CR (CRLF-encoded pointers).
    /// </summary>
    public static bool IsPointer(string? content)
    {
        if (string.IsNullOrEmpty(content)) return false;
        var end = content.IndexOf('\n');
        var firstLine = (end < 0 ? content : content.Substring(0, end)).TrimEnd('\r');
        return firstLine == VersionLine;
    }

    /// <summary>
    /// The <c>size</c> field (bytes) from a pointer, or null when absent/unparsable. Lets the diff
    /// viewer show a human-readable size in the "LFS object (size)" summary.
    /// </summary>
    public static long? ParseSize(string? content)
    {
        if (!IsPointer(content)) return null;
        foreach (var raw in content!.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            const string prefix = "size ";
            if (line.StartsWith(prefix, StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(prefix.Length), out var size))
                return size;
        }
        return null;
    }

    /// <summary>The <c>oid sha256:…</c> field from a pointer (the hex OID), or null when absent.</summary>
    public static string? ParseOid(string? content)
    {
        if (!IsPointer(content)) return null;
        foreach (var raw in content!.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            const string prefix = "oid sha256:";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line.Substring(prefix.Length);
        }
        return null;
    }
}
