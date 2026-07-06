using System;
using System.Collections.Generic;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure helpers for the image-diff path (T-13). Detects whether a changed file is an image the
/// viewer should render as a before/after image pair (vs. an opaque binary), and formats the
/// size-change summary shown for non-image binaries. No repo, no IO, no Avalonia — table-testable.
/// </summary>
public static class ImageDiffDetection
{
    /// <summary>Extensions the image-diff control knows how to render (lower-case, no dot).</summary>
    public static readonly IReadOnlySet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "gif", "bmp", "webp", "ico"
    };

    /// <summary>
    /// True when a changed blob should be shown as an image diff: it must be flagged binary AND
    /// carry a known image extension. A text file with an image extension (not binary) or a binary
    /// with a non-image extension both return false.
    /// </summary>
    public static bool IsImageCandidate(string? path, bool isBinary)
    {
        if (!isBinary || string.IsNullOrEmpty(path)) return false;

        var ext = System.IO.Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return false;
        return ImageExtensions.Contains(ext.TrimStart('.'));
    }

    /// <summary>
    /// Recognizes the markers git emits for a binary diff body so the viewer can switch to the
    /// image / binary-summary path instead of trying to render the (absent) textual hunks.
    /// </summary>
    public static bool DiffIndicatesBinary(string? diffContent)
    {
        if (string.IsNullOrEmpty(diffContent)) return false;
        return diffContent.Contains("GIT binary patch", StringComparison.Ordinal)
            || diffContent.Contains("Binary files ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Cheap binary sniff over a leading byte sample: a NUL byte never appears in UTF-8/16 text but
    /// is ubiquitous in image/binary payloads. Used as a fallback when the diff body itself doesn't
    /// carry git's binary marker. Empty sample → false.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        foreach (var b in sample)
            if (b == 0) return true;
        return false;
    }

    /// <summary>Human-readable "old → new" summary for a non-image binary change.</summary>
    public static string FormatBinarySummary(long oldSize, long newSize)
        => $"Binary file changed ({FormatSize(oldSize)} → {FormatSize(newSize)})";

    private static string FormatSize(long bytes)
    {
        if (bytes < 0) return "unknown";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{size.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
