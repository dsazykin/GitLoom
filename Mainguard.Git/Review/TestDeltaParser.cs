using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Mainguard.Git.Review;

/// <summary>One test's outcome from a verification log.</summary>
public sealed record TestOutcome(string Name, bool Passed);

/// <summary>
/// The test-delta strip's data (P2-11 §3.5): what the branch run newly fails and newly passes versus the
/// latest main-baseline run, plus the branch run's headline counts. Pure projection.
/// </summary>
public sealed record TestDelta(
    IReadOnlyList<string> NewFailures,
    IReadOnlyList<string> NewPasses,
    int TotalCurrent,
    int PassedCurrent,
    int FailedCurrent);

/// <summary>
/// Pure verification-log parser (P2-11 §3.5). No repo, no IO. Reads the .NET TRX/xUnit output the
/// verification command produces when available and falls back to a plain <c>name PASS|FAIL</c> list;
/// <see cref="Compute"/> diffs a branch run against the main baseline. Tolerant of malformed input (a bad
/// TRX yields an empty outcome list, never a throw).
/// </summary>
public static class TestDeltaParser
{
    /// <summary>Parses a VSTest TRX document into per-test outcomes (namespace-tolerant); malformed → empty.</summary>
    public static IReadOnlyList<TestOutcome> ParseTrx(string trxXml)
    {
        var outcomes = new List<TestOutcome>();
        if (string.IsNullOrWhiteSpace(trxXml))
        {
            return outcomes;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(trxXml);
        }
        catch (System.Xml.XmlException)
        {
            return outcomes;
        }

        foreach (var result in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var name = (string?)result.Attribute("testName");
            var outcome = (string?)result.Attribute("outcome");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            outcomes.Add(new TestOutcome(name!, string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase)));
        }

        return outcomes;
    }

    /// <summary>Parses a plain <c>TestName PASS|FAIL</c> (or <c>PASS|FAIL TestName</c>) list, one per line.</summary>
    public static IReadOnlyList<TestOutcome> ParsePassFail(string text)
    {
        var outcomes = new List<TestOutcome>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return outcomes;
        }

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            bool? passed = null;
            string? name = null;

            var tokens = line.Split(new[] { ' ', '\t' }, 2, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 2)
            {
                if (TryVerdict(tokens[0], out var leading))
                {
                    passed = leading;
                    name = tokens[1].Trim();
                }
                else if (TryVerdict(tokens[1], out var trailing))
                {
                    passed = trailing;
                    name = tokens[0].Trim();
                }
            }

            name = name?.TrimEnd(':').Trim();
            if (passed is not null && !string.IsNullOrWhiteSpace(name))
            {
                outcomes.Add(new TestOutcome(name!, passed.Value));
            }
        }

        return outcomes;
    }

    /// <summary>
    /// Computes the delta of a branch run versus the main baseline: new failures (passing/absent in
    /// baseline, failing now), new passes (failing in baseline, passing now), plus branch headline counts.
    /// </summary>
    public static TestDelta Compute(IReadOnlyList<TestOutcome> current, IReadOnlyList<TestOutcome> baseline)
    {
        current ??= Array.Empty<TestOutcome>();
        baseline ??= Array.Empty<TestOutcome>();

        // Last-writer-wins if a name repeats (defensive); ordinal names.
        var baseByName = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var t in baseline)
        {
            baseByName[t.Name] = t.Passed;
        }

        var newFailures = new List<string>();
        var newPasses = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passedCount = 0;

        foreach (var t in current)
        {
            if (!seen.Add(t.Name))
            {
                continue;
            }

            if (t.Passed)
            {
                passedCount++;
            }

            var hadBaseline = baseByName.TryGetValue(t.Name, out var wasPassing);
            if (!t.Passed && (!hadBaseline || wasPassing))
            {
                newFailures.Add(t.Name);
            }
            else if (t.Passed && hadBaseline && !wasPassing)
            {
                newPasses.Add(t.Name);
            }
        }

        var total = seen.Count;
        return new TestDelta(newFailures, newPasses, total, passedCount, total - passedCount);
    }

    private static bool TryVerdict(string token, out bool passed)
    {
        token = token.Trim().TrimEnd(':').ToUpperInvariant();
        switch (token)
        {
            case "PASS":
            case "PASSED":
            case "OK":
                passed = true;
                return true;
            case "FAIL":
            case "FAILED":
            case "ERROR":
                passed = false;
                return true;
            default:
                passed = false;
                return false;
        }
    }
}
