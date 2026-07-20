using System;
using System.Collections.Generic;

namespace Mainguard.Git.Models;

// Structured unified-diff model consumed by PatchParser/PatchBuilder (T-06).
// Pure data — no repo/IO. One FilePatch per `diff --git` section.

public enum DiffLineKind { Context, Add, Delete }

public sealed class DiffLine
{
    public DiffLineKind Kind { get; init; }
    public string Text { get; init; } = "";      // WITHOUT the +/-/space prefix
    public bool NoNewlineAtEof { get; init; }     // "\ No newline at end of file" applies to this line
}

public sealed class DiffHunk
{
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }
    public string SectionHeading { get; init; } = "";   // text after the second @@ (incl. any leading space git emitted)
    public IReadOnlyList<DiffLine> Lines { get; init; } = Array.Empty<DiffLine>();

    // git omits a ",1" count in the hunk header (`@@ -3 +3 @@`). These record the
    // original short/long form per side so Serialize round-trips byte-identically.
    public bool OldCountOmitted { get; init; }
    public bool NewCountOmitted { get; init; }
}

public sealed class FilePatch
{
    public string Header { get; init; } = "";           // everything before the first @@ (diff --git, index, ---, +++, rename/mode lines)
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
}
