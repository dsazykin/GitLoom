using System.Collections.Generic;

namespace Mainguard.Git.Models;

public class GitDiffLine
{
    public string Content { get; set; } = string.Empty;
    public char LineType { get; set; }

    // Helpers for our Avalonia UI styles
    public bool IsAdded => LineType == '+';
    public bool IsRemoved => LineType == '-';
    public bool IsHeader => LineType == '@';

    // Intra-line (word-level) emphasis (T-13): character sub-ranges of <see cref="Content"/> that
    // actually changed vs. the paired line. Offsets are UTF-16 indices INTO Content (i.e. they
    // already account for the leading +/-/space prefix). Empty when the line is unchanged or has no
    // paired counterpart. Computed by IntraLineDiff; rendered as darker runs by IntraLineDiffTextBlock.
    public List<(int Start, int Length)> HighlightSpans { get; set; } = new();

    // Trailing-whitespace marker (T-13): the (Start, Length) run of trailing whitespace in Content,
    // or null when there is none. Rendered with a distinct tint so invisible trailing spaces/tabs show.
    public (int Start, int Length)? TrailingWhitespaceSpan { get; set; }

    // Theme token key for the intra-line emphasis brush, chosen by line role (added vs removed).
    public string EmphasisKey => IsAdded ? "DiffAddedEmphasis" : IsRemoved ? "DiffRemovedEmphasis" : "DiffAddedEmphasis";
}
