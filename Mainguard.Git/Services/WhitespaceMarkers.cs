namespace Mainguard.Git.Services;

/// <summary>
/// Pure trailing-whitespace detection for diff quality (T-13). Strings in, span out — no repo,
/// no IO, no Avalonia. The diff viewer uses the returned range to tint the trailing whitespace on
/// a line so otherwise-invisible spaces/tabs at the end of an added line become visible.
/// </summary>
public static class WhitespaceMarkers
{
    /// <summary>
    /// Returns the (Start, Length) of the run of trailing whitespace on <paramref name="line"/>,
    /// or <c>null</c> when there is none.
    ///   - empty string            → null (nothing to mark)
    ///   - "code   "               → the trailing spaces after the last non-blank char
    ///   - "\t\t" (all whitespace) → the whole line (git flags a blank-but-whitespace line too)
    ///   - "code" (no trailing)    → null
    /// Offsets are UTF-16 indices into <paramref name="line"/>.
    /// </summary>
    public static (int Start, int Length)? TrailingWhitespace(string? line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        int lastNonWs = -1;
        for (int i = line.Length - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(line[i])) { lastNonWs = i; break; }
        }

        // Whole line is whitespace: flag all of it.
        if (lastNonWs < 0) return (0, line.Length);

        // No trailing whitespace.
        if (lastNonWs == line.Length - 1) return null;

        int start = lastNonWs + 1;
        return (start, line.Length - start);
    }
}
