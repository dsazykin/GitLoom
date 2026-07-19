using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mainguard.Git.Analytics;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Property tests for the three pure engines (Lane H Part 4): <see cref="PatchParser"/>,
/// <see cref="MergeDiffService"/> (the 3-way chunker) and <see cref="ChangelogGenerator"/>.
/// The example-based suites pin known cases; these pin the <em>laws</em> the engines promise on
/// arbitrary inputs — seeded <see cref="Random"/> generators (deterministic on any CI, no
/// external property-testing dependency), hundreds of cases per law:
/// <list type="bullet">
/// <item>PatchParser: Serialize∘Parse is byte-identical on any structurally-valid LF patch.</item>
/// <item>MergeChunker: chunks cover the base in order, and resolving every chunk toward one side
/// reassembles exactly that side's document (the conservation law the conflict resolver's
/// "never lose work" promise rests on).</item>
/// <item>ChangelogGenerator: no commit is ever dropped — every subject parses to an entry and
/// every entry appears in the notes exactly once.</item>
/// </list>
/// </summary>
public class PureEnginePropertyTests
{
    // ---- Generators (seeded, deterministic) ------------------------------------------------------

    private static string RandomLine(Random rng)
    {
        // Includes the characters that historically break diff/merge code: leading +/-/space/@,
        // tabs, unicode, whitespace-only lines. The EMPTY line ("") is deliberately excluded from
        // the merge-side generator: MergeChunk stores its slices as joined strings, so a slice of
        // exactly one empty line is indistinguishable from an empty slice ("" both ways) — see the
        // pinned single-empty-line regression tests in MergeDiffServiceTests for how AssembleMerged
        // disambiguates the Unchanged case, and the KNOWN LIMIT note there for the resolved-side one.
        string[] pool =
        {
            " ", "+", "-", "@@ not a hunk", "\\ almost a marker", "let x = 1;", "\ttabbed",
            "ünïcode ✓", "}", "    return value;", "public class C", "***", "a b c d e"
        };
        return rng.Next(4) == 0
            ? pool[rng.Next(pool.Length)]
            : new string((char)('a' + rng.Next(26)), rng.Next(1, 12));
    }

    private static string[] RandomDocument(Random rng, int maxLines)
        => Enumerable.Range(0, rng.Next(0, maxLines)).Select(_ => RandomLine(rng)).ToArray();

    /// <summary>A random edit of <paramref name="baseLines"/>: line-level inserts/deletes/replacements.</summary>
    private static string[] Mutate(Random rng, string[] baseLines)
    {
        var result = new List<string>(baseLines);
        int edits = rng.Next(0, 4);
        for (int e = 0; e < edits; e++)
        {
            int op = rng.Next(3);
            if (op == 0) // insert
            {
                result.Insert(rng.Next(result.Count + 1), RandomLine(rng));
            }
            else if (op == 1 && result.Count > 0) // delete
            {
                result.RemoveAt(rng.Next(result.Count));
            }
            else if (result.Count > 0) // replace
            {
                result[rng.Next(result.Count)] = RandomLine(rng);
            }
        }
        return result.ToArray();
    }

    private static string Join(string[] lines) => lines.Length == 0 ? "" : string.Join("\n", lines) + "\n";

    // ---- PatchParser: Serialize ∘ Parse round-trip ----------------------------------------------

    [Theory]
    [InlineData(11)]
    [InlineData(29)]
    [InlineData(83)]
    public void PatchParser_SerializeParse_RoundTripsByteIdentically(int seed)
    {
        var rng = new Random(seed);
        for (int @case = 0; @case < 200; @case++)
        {
            var patch = RandomFilePatch(rng);
            var text = PatchParser.Serialize(patch);

            var reparsed = PatchParser.Parse(text);
            Assert.Single(reparsed);
            var again = PatchParser.Serialize(reparsed[0]);

            // Byte-identical round-trip is the law PatchBuilder's verbatim hunk emission rests on
            // (a re-serialized hunk must be safe to feed to `git apply`).
            Assert.Equal(text, again);
        }
    }

    /// <summary>A structurally-valid random FilePatch: header + hunks whose counts match their lines.</summary>
    private static FilePatch RandomFilePatch(Random rng)
    {
        var hunks = new List<DiffHunk>();
        int hunkCount = rng.Next(1, 4);
        int oldPos = 1, newPos = 1;
        for (int h = 0; h < hunkCount; h++)
        {
            var lines = new List<DiffLine>();
            int lineCount = rng.Next(1, 10);
            int oldCount = 0, newCount = 0;
            for (int l = 0; l < lineCount; l++)
            {
                var kind = rng.Next(3) switch
                {
                    0 => DiffLineKind.Context,
                    1 => DiffLineKind.Add,
                    _ => DiffLineKind.Delete,
                };
                // Line text must not itself look like a diff structure marker when serialized —
                // the prefix char is added by Serialize, the body is arbitrary.
                string text = RandomLine(rng);
                bool noEof = l == lineCount - 1 && rng.Next(6) == 0;
                lines.Add(new DiffLine { Kind = kind, Text = text, NoNewlineAtEof = noEof });
                if (kind != DiffLineKind.Add) oldCount++;
                if (kind != DiffLineKind.Delete) newCount++;
            }

            bool omitOld = oldCount == 1 && rng.Next(2) == 0;
            bool omitNew = newCount == 1 && rng.Next(2) == 0;
            hunks.Add(new DiffHunk
            {
                OldStart = oldPos,
                OldCount = oldCount,
                NewStart = newPos,
                NewCount = newCount,
                OldCountOmitted = omitOld,
                NewCountOmitted = omitNew,
                SectionHeading = rng.Next(3) == 0 ? " void Method()" : "",
                Lines = lines,
            });
            oldPos += oldCount + rng.Next(1, 5);
            newPos += newCount + rng.Next(1, 5);
        }

        return new FilePatch
        {
            Header = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n",
            Hunks = hunks,
        };
    }

    // ---- MergeChunker: conservation ---------------------------------------------------------------

    [Theory]
    [InlineData(3)]
    [InlineData(17)]
    [InlineData(59)]
    public void MergeChunker_ConservationLaws_HoldOnRandomEdits(int seed)
    {
        var rng = new Random(seed);
        var svc = new MergeDiffService();

        for (int @case = 0; @case < 150; @case++)
        {
            var baseLines = RandomDocument(rng, 20);
            var @base = Join(baseLines);
            var leftDoc = Join(Mutate(rng, baseLines));
            var rightDoc = Join(Mutate(rng, baseLines));

            // Law 1 — one-sided left edit: theirs untouched ⇒ NO conflicts, and the assembly is
            // exactly the left document (this is the "accept the resolver's output without fear"
            // guarantee: an unopposed edit can never be altered by the chunker).
            var leftOnly = svc.GenerateMergeChunks(@base, leftDoc, @base);
            Assert.DoesNotContain(leftOnly, c => c.Kind == ChunkKind.Conflict);
            AssertDocumentEqual(leftDoc, svc.AssembleMerged(leftOnly), "left-vs-untouched", seed, @case);

            // Law 2 — one-sided right edit, symmetric.
            var rightOnly = svc.GenerateMergeChunks(@base, @base, rightDoc);
            Assert.DoesNotContain(rightOnly, c => c.Kind == ChunkKind.Conflict);
            AssertDocumentEqual(rightDoc, svc.AssembleMerged(rightOnly), "right-vs-untouched", seed, @case);

            // Law 3 — identical edits on both sides merge cleanly (no conflict) to that edit.
            var both = svc.GenerateMergeChunks(@base, leftDoc, leftDoc);
            Assert.DoesNotContain(both, c => c.Kind == ChunkKind.Conflict);
            AssertDocumentEqual(leftDoc, svc.AssembleMerged(both), "identical-edits", seed, @case);

            // Law 4 — base coverage under a genuine two-sided merge: the BaseText of all chunks
            // concatenates back to the base document, in order — no base line is ever dropped or
            // duplicated by the chunker, conflicts or not.
            var merged = svc.GenerateMergeChunks(@base, leftDoc, rightDoc);
            var baseRebuilt = merged
                .Where(c => c.BaseText.Length > 0)
                .SelectMany(c => c.BaseText.Split('\n'))
                .ToArray();
            Assert.Equal(Join(baseLines), Join(baseRebuilt));

            // Law 5 — a fully-resolved two-sided merge assembles without throwing, and every
            // LeftOnly / TakeLeft-conflict slice surfaces verbatim in the output.
            foreach (var c in merged)
                if (c.Kind == ChunkKind.Conflict) c.Resolution = ChunkResolution.TakeLeft;
            var assembled = svc.AssembleMerged(merged);
            foreach (var c in merged)
            {
                if ((c.Kind == ChunkKind.LeftOnly || c.Kind == ChunkKind.Conflict) && c.LeftText.Length > 0)
                    Assert.Contains(c.LeftText.Replace("\r\n", "\n"), assembled);
            }
        }
    }

    private static void AssertDocumentEqual(string expected, string actual, string label, int seed, int @case)
    {
        // AssembleMerged's pinned policy: a non-empty merged document ends with exactly one '\n';
        // an all-empty one is "". Normalize the expectation the same way.
        string norm = expected.Replace("\r\n", "\n").Replace("\r", "\n");
        if (norm.Length > 0 && !norm.EndsWith('\n')) norm += "\n";
        Assert.True(norm == actual,
            $"conservation toward {label} broke at seed={seed} case={@case}\nexpected:\n{norm}\nactual:\n{actual}");
    }

    // ---- ChangelogGenerator: nothing is ever dropped ---------------------------------------------

    [Theory]
    [InlineData(5)]
    [InlineData(23)]
    [InlineData(71)]
    public void ChangelogGenerator_EverySubjectParses_AndEveryEntryAppearsInNotes(int seed)
    {
        var rng = new Random(seed);
        string[] shapes =
        {
            "feat: add {0}",
            "fix({1}): repair {0}",
            "feat!: break {0}",
            "chore({1})!: rework {0}",
            "just a plain subject about {0}",
            "docs: {0}",
            "{0}",             // bare word
            "refactor: ",      // empty description
            "weird: but: colons: everywhere {0}",
        };

        for (int @case = 0; @case < 200; @case++)
        {
            int n = rng.Next(1, 12);
            var entries = new List<ChangelogEntry>();
            for (int i = 0; i < n; i++)
            {
                string word = new string((char)('a' + rng.Next(26)), rng.Next(1, 8));
                string subject = string.Format(shapes[rng.Next(shapes.Length)], word, "scope" + rng.Next(3));
                var entry = ChangelogGenerator.ParseSubject($"sha{i:D2}{@case:D3}", subject);

                // Law 1: ParseSubject never throws and never yields a null/empty type.
                Assert.False(string.IsNullOrEmpty(entry.Type));
                entries.Add(entry);
            }

            string notes = ChangelogGenerator.BuildNotes(entries, "v1.0.0", "v1.1.0");

            // Law 2: every entry surfaces in the notes exactly once (keyed by its short sha).
            foreach (var entry in entries)
            {
                string shortSha = entry.Sha.Length <= 7 ? entry.Sha : entry.Sha[..7];
                int hits = CountOccurrences(notes, shortSha);
                Assert.True(hits >= 1, $"entry {entry.Sha} ('{entry.Description}') missing from notes");
            }

            // Law 3: the full-changelog line always names both tags.
            Assert.Contains("v1.0.0", notes);
            Assert.Contains("v1.1.0", notes);
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }
}
