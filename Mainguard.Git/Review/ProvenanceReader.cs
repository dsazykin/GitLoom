using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mainguard.Git.Models;

namespace Mainguard.Git.Review;

/// <summary>
/// Per-hunk provenance (P2-11 contract §2). <see cref="Source"/> is either <c>"agent-trace"</c> (the
/// Cognition/Cursor interchange JSON — Mainguard is the first review UI to render it) or <c>"trailer"</c>
/// (the durable <c>Agent:</c>/<c>Task:</c>/<c>Plan:</c> commit trailers). Every field but
/// <see cref="Sha"/>/<see cref="Source"/> is nullable — a human commit yields null fields, never a crash.
/// </summary>
public sealed record HunkProvenance(string? Agent, string? Task, string? Plan, string Sha, string Source);

/// <summary>One Agent-Trace contribution range joined to its provenance (the range-join seam).</summary>
public sealed record AgentTraceRange(string File, int StartLine, int EndLine, HunkProvenance Provenance);

/// <summary>
/// Pure provenance reader (P2-11 step 2). No repo, no IO, <b>no network</b> (a rejection trigger). Every
/// parse path is tolerant of missing/unknown/malformed input: bad JSON yields an empty list, a
/// trailer-less commit yields null — never an exception (edge row 3).
/// </summary>
public static class ProvenanceReader
{
    /// <summary>Parses an Agent-Trace document into per-range provenance; malformed input → empty list.</summary>
    public static IReadOnlyList<HunkProvenance> FromAgentTrace(string traceJson) =>
        ParseTraceRanges(traceJson).Select(r => r.Provenance).ToList();

    /// <summary>
    /// Parses an Agent-Trace document into contribution ranges (our shape and common external-vendor
    /// shapes). Tolerant of unknown fields and either an <c>entries</c>/<c>ranges</c>/<c>contributions</c>
    /// array or a bare top-level array. Any malformed input returns an empty list — never throws.
    /// </summary>
    public static IReadOnlyList<AgentTraceRange> ParseTraceRanges(string traceJson)
    {
        var result = new List<AgentTraceRange>();
        if (string.IsNullOrWhiteSpace(traceJson))
        {
            return result;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(traceJson);
        }
        catch (JsonException)
        {
            return result; // malformed → typed empty, not a crash.
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Session-level fields (external vendor traces attach the contributor once, at the top).
            var sessionAgent = TryStringAny(root, "agent", "author", "contributor", "agentName");
            var sessionTask = TryStringAny(root, "task", "taskId", "task_id", "ticket");
            var sessionPlan = TryStringAny(root, "plan", "planId", "plan_id");
            var sessionSha = TryStringAny(root, "sha", "commit", "commitSha", "revision") ?? string.Empty;

            var array = FindArray(root);
            if (array is null)
            {
                return result;
            }

            foreach (var item in array.Value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var file = TryStringAny(item, "file", "path", "filePath", "filename");
                if (string.IsNullOrWhiteSpace(file))
                {
                    continue; // a range with no file cannot be joined; skip it defensively.
                }

                var (start, end) = ReadRange(item);
                var agent = TryStringAny(item, "agent", "author", "contributor", "agentName") ?? sessionAgent;
                var task = TryStringAny(item, "task", "taskId", "task_id", "ticket") ?? sessionTask;
                var plan = TryStringAny(item, "plan", "planId", "plan_id") ?? sessionPlan;
                var sha = TryStringAny(item, "sha", "commit", "commitSha", "revision") ?? sessionSha;

                result.Add(new AgentTraceRange(
                    file!,
                    start,
                    end,
                    new HunkProvenance(agent, task, plan, sha, "agent-trace")));
            }
        }

        return result;
    }

    /// <summary>
    /// The provenance for a specific hunk of a file: the trace range that overlaps the hunk's new-file
    /// line span (widest overlap wins), or null when nothing matches (a human-authored hunk).
    /// </summary>
    public static HunkProvenance? ForHunk(IReadOnlyList<AgentTraceRange> ranges, string filePath, DiffHunk hunk)
    {
        if (ranges is null || ranges.Count == 0 || hunk is null)
        {
            return null;
        }

        var normalized = NormalizePath(filePath);
        var hunkStart = hunk.NewStart;
        var hunkEnd = hunk.NewStart + Math.Max(hunk.NewCount, 1) - 1;

        HunkProvenance? best = null;
        var bestOverlap = 0;
        foreach (var range in ranges)
        {
            if (NormalizePath(range.File) != normalized)
            {
                continue;
            }

            var overlap = Math.Min(hunkEnd, range.EndLine) - Math.Max(hunkStart, range.StartLine) + 1;
            if (overlap > 0 && overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = range.Provenance;
            }
        }

        return best;
    }

    /// <summary>
    /// Parses the three <c>Agent:</c>/<c>Task:</c>/<c>Plan:</c> trailers from a commit message. Returns
    /// null when none are present (a human commit — provenance is simply absent, no crash). Partial sets
    /// yield a record with the missing fields null (edge row 3).
    /// </summary>
    public static HunkProvenance? FromTrailers(string commitMessage, string sha)
    {
        if (string.IsNullOrEmpty(commitMessage))
        {
            return null;
        }

        string? agent = null, task = null, plan = null;
        var lines = commitMessage.Replace("\r\n", "\n").Split('\n');
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (value.Length == 0)
            {
                continue;
            }

            if (key.Equals("Agent", StringComparison.OrdinalIgnoreCase))
            {
                agent = value;
            }
            else if (key.Equals("Task", StringComparison.OrdinalIgnoreCase))
            {
                task = value;
            }
            else if (key.Equals("Plan", StringComparison.OrdinalIgnoreCase))
            {
                plan = value;
            }
        }

        if (agent is null && task is null && plan is null)
        {
            return null;
        }

        return new HunkProvenance(agent, task, plan, sha ?? string.Empty, "trailer");
    }

    private static JsonElement? FindArray(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "entries", "ranges", "contributions", "attributions", "spans" })
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static (int Start, int End) ReadRange(JsonElement item)
    {
        var start = TryIntAny(item, "startLine", "start", "start_line", "from", "lineStart") ?? 0;
        var end = TryIntAny(item, "endLine", "end", "end_line", "to", "lineEnd") ?? start;

        // Some traces carry a nested { "range": { "start": .., "end": .. } }.
        if (item.TryGetProperty("range", out var nested) && nested.ValueKind == JsonValueKind.Object)
        {
            start = TryIntAny(nested, "start", "startLine", "from") ?? start;
            end = TryIntAny(nested, "end", "endLine", "to") ?? end;
        }

        if (end < start)
        {
            end = start;
        }

        return (start, end);
    }

    private static string? TryStringAny(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.String)
                {
                    var s = value.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        return s;
                    }
                }
                else if (value.ValueKind is JsonValueKind.Number)
                {
                    return value.ToString();
                }
            }
        }

        return null;
    }

    private static int? TryIntAny(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n))
                {
                    return n;
                }

                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    private static string NormalizePath(string? path) =>
        (path ?? string.Empty).Replace('\\', '/').Trim().TrimStart('/');
}
