using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure builder of unified-diff subsets (T-06). No repo, no IO. Feeds the existing
/// <c>StageHunk</c>/<c>UnstageHunk</c>/<c>DiscardHunk</c> (which shell to <c>git apply</c>).
/// An empty selection yields <c>""</c>, which those methods treat as a no-op.
/// </summary>
public static class PatchBuilder
{
    /// <summary>Emits the file header plus the selected hunks serialized verbatim, in original order.</summary>
    public static string BuildHunkPatch(FilePatch file, IReadOnlyList<int> selectedHunkIndexes)
    {
        if (selectedHunkIndexes == null || selectedHunkIndexes.Count == 0) return "";
        var wanted = new HashSet<int>(selectedHunkIndexes);

        var selected = new List<DiffHunk>();
        for (int i = 0; i < file.Hunks.Count; i++)
            if (wanted.Contains(i)) selected.Add(file.Hunks[i]);

        if (selected.Count == 0) return "";
        return PatchParser.Serialize(new FilePatch { Header = file.Header, Hunks = selected });
    }

    /// <summary>
    /// Builds a single-hunk patch containing only the selected add/delete lines within
    /// <paramref name="hunkIndex"/> (the <c>git add -p</c> split): unselected adds are dropped,
    /// unselected deletes become context, and the header counts are recomputed from the result.
    /// Returns <c>""</c> when no add/delete line is selected.
    /// </summary>
    public static string BuildLinePatch(FilePatch file, int hunkIndex, IReadOnlyList<int> selectedLineIndexes)
    {
        if (hunkIndex < 0 || hunkIndex >= file.Hunks.Count) return "";
        var hunk = file.Hunks[hunkIndex];
        var selected = selectedLineIndexes == null
            ? new HashSet<int>()
            : new HashSet<int>(selectedLineIndexes);

        var resultLines = new List<DiffLine>();
        bool anyChangeSelected = false;

        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            bool sel = selected.Contains(i);

            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    resultLines.Add(line);
                    break;

                case DiffLineKind.Add:
                    if (sel) { resultLines.Add(line); anyChangeSelected = true; }
                    // unselected add -> dropped entirely
                    break;

                case DiffLineKind.Delete:
                    if (sel) { resultLines.Add(line); anyChangeSelected = true; }
                    else
                    {
                        // unselected delete -> the old text stays, so it becomes context
                        resultLines.Add(new DiffLine
                        {
                            Kind = DiffLineKind.Context,
                            Text = line.Text,
                            NoNewlineAtEof = line.NoNewlineAtEof
                        });
                    }
                    break;
            }
        }

        if (!anyChangeSelected) return "";

        int oldCount = 0, newCount = 0;
        foreach (var l in resultLines)
        {
            switch (l.Kind)
            {
                case DiffLineKind.Context: oldCount++; newCount++; break;
                case DiffLineKind.Delete: oldCount++; break;
                case DiffLineKind.Add: newCount++; break;
            }
        }

        var built = new DiffHunk
        {
            OldStart = hunk.OldStart,
            OldCount = oldCount,
            NewStart = hunk.NewStart,   // reference choice; git apply tolerates it (correctness pinned by integration tests)
            NewCount = newCount,
            SectionHeading = hunk.SectionHeading,
            Lines = resultLines,
            OldCountOmitted = false,    // always emit explicit counts in built subsets
            NewCountOmitted = false
        };

        return PatchParser.Serialize(new FilePatch { Header = file.Header, Hunks = new[] { built } });
    }
}
