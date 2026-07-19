using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Mainguard.Git.Review;

namespace GitLoom.Core.Agents.Orchestrator;

/// <summary>One file/line-range contribution in an Agent Trace record.</summary>
public sealed record AgentTraceContribution(string File, int StartLine, int EndLine);

/// <summary>
/// A worker session's Agent Trace: the agent/task/plan/session identity plus its file/line-range
/// contributions. Serialized to the Cognition/Cursor-style interchange JSON that
/// <see cref="ProvenanceReader.ParseTraceRanges"/> reads back.
/// </summary>
public sealed record AgentTraceSession(
    string Agent,
    string Task,
    string Plan,
    string Session,
    string Sha,
    IReadOnlyList<AgentTraceContribution> Contributions);

/// <summary>
/// Orchestrator-side provenance emitter (P2-11 step 2). Every worker session emits an <b>Agent Trace</b>
/// JSON artifact (file/line ranges → agent/task/plan/session) to the daemon artifact dir, <b>and</b> writes
/// durable <c>Agent:</c>/<c>Task:</c>/<c>Plan:</c> commit trailers as the in-history fallback. The read
/// side (<see cref="ProvenanceReader"/>) is pure; this write side owns the small amount of IO.
/// </summary>
public sealed class AgentTraceEmitter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _artifactDir;

    /// <param name="artifactDir">The daemon-owned directory Agent Trace artifacts are written to (one file per branch).</param>
    public AgentTraceEmitter(string artifactDir) =>
        _artifactDir = artifactDir ?? throw new ArgumentNullException(nameof(artifactDir));

    /// <summary>Writes the branch's Agent Trace artifact and returns its path.</summary>
    public string EmitTrace(string branch, AgentTraceSession session)
    {
        Directory.CreateDirectory(_artifactDir);
        var path = Path.Combine(_artifactDir, $"agent-trace_{Sanitize(branch)}.json");
        File.WriteAllText(path, SerializeTrace(session));
        return path;
    }

    /// <summary>Serializes a session to the Agent Trace interchange JSON (round-trips through <see cref="ProvenanceReader"/>).</summary>
    public static string SerializeTrace(AgentTraceSession session)
    {
        var doc = new
        {
            version = "1",
            agent = session.Agent,
            task = session.Task,
            plan = session.Plan,
            session = session.Session,
            sha = session.Sha,
            entries = session.Contributions.Select(c => new
            {
                file = c.File,
                startLine = c.StartLine,
                endLine = c.EndLine,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(doc, SerializerOptions);
    }

    /// <summary>
    /// Appends the three provenance trailers to a commit message (the durable in-history fallback). Idempotent:
    /// an existing trailer of a given key is not duplicated. Empty values are skipped. Pure — no IO.
    /// </summary>
    public static string BuildTrailers(string? commitMessage, string? agent, string? task, string? plan)
    {
        var message = (commitMessage ?? string.Empty).Replace("\r\n", "\n").TrimEnd('\n');
        var existing = message.Length == 0
            ? Array.Empty<string>()
            : message.Split('\n');

        var toAdd = new List<string>();
        AddIfMissing(toAdd, existing, "Agent", agent);
        AddIfMissing(toAdd, existing, "Task", task);
        AddIfMissing(toAdd, existing, "Plan", plan);

        if (toAdd.Count == 0)
        {
            return message;
        }

        var sb = new StringBuilder(message);
        // A blank line separates the body from the trailer block (git-trailer convention), unless the
        // message is already a bare set of trailers.
        var lastLineIsTrailer = existing.Length > 0 && IsTrailerLine(existing[^1]);
        if (message.Length > 0 && !lastLineIsTrailer)
        {
            sb.Append('\n');
        }

        if (message.Length > 0)
        {
            sb.Append('\n');
        }

        sb.Append(string.Join('\n', toAdd));
        return sb.ToString();
    }

    private static void AddIfMissing(List<string> toAdd, IReadOnlyList<string> existing, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var prefix = key + ":";
        if (existing.Any(l => l.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        toAdd.Add($"{key}: {value.Trim()}");
    }

    private static bool IsTrailerLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("Agent:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Task:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("Plan:", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string s) =>
        new(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
