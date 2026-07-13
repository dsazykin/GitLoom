using System.Collections.Generic;
using System.Linq;
using GitLoom.Core.Agents.Orchestrator;
using GitLoom.Core.Models;
using GitLoom.Core.Review;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-11 tests 2 (Trailer_ParseMatrix) + 3 (AgentTrace_ParseAndRangeJoin). The reader is pure and
/// <b>never</b> throws on missing/unknown/malformed input (edge row 3) and never touches the network
/// (rejection trigger). Also pins the <see cref="AgentTraceEmitter"/> write side round-tripping through
/// the reader.
/// </summary>
public class ProvenanceReaderTests
{
    // ---- Trailer parse matrix -------------------------------------------------

    [Fact]
    public void Trailers_AllThreePresent_MapToFields()
    {
        var msg = "Fix auth refresh\n\nAgent: Loom-3\nTask: P2-11\nPlan: plan-7";
        var p = ProvenanceReader.FromTrailers(msg, "a1b2c3d");

        Assert.NotNull(p);
        Assert.Equal("Loom-3", p!.Agent);
        Assert.Equal("P2-11", p.Task);
        Assert.Equal("plan-7", p.Plan);
        Assert.Equal("a1b2c3d", p.Sha);
        Assert.Equal("trailer", p.Source);
    }

    [Fact]
    public void Trailers_Partial_LeaveMissingFieldsNull()
    {
        var p = ProvenanceReader.FromTrailers("Subject\n\nAgent: Loom-1", "sha1");
        Assert.NotNull(p);
        Assert.Equal("Loom-1", p!.Agent);
        Assert.Null(p.Task);
        Assert.Null(p.Plan);
    }

    [Fact]
    public void Trailers_HumanCommitWithNone_ReturnsNull()
    {
        Assert.Null(ProvenanceReader.FromTrailers("Just a normal human commit\n\nwith a body.", "sha"));
        Assert.Null(ProvenanceReader.FromTrailers("", "sha"));
    }

    [Fact]
    public void Trailers_Malformed_DoNotThrow()
    {
        // Colons in odd places, no values — parsed defensively, never an exception.
        var p = ProvenanceReader.FromTrailers("::::\nAgent:\nTask:   \nrandom: text", "sha");
        Assert.Null(p); // no non-empty Agent/Task/Plan value
    }

    // ---- Agent Trace parse + range join --------------------------------------

    [Fact]
    public void AgentTrace_OurShape_ParsesRangesAndJoinsToHunk()
    {
        var json = """
        {
          "version": "1",
          "session": "sess-9",
          "sha": "c0ffee",
          "entries": [
            { "file": "src/Auth.cs", "startLine": 10, "endLine": 20, "agent": "Loom-3", "task": "P2-11", "plan": "plan-7" },
            { "file": "src/Other.cs", "startLine": 1, "endLine": 5, "agent": "Loom-4", "task": "P2-12", "plan": "plan-8" }
          ]
        }
        """;

        var ranges = ProvenanceReader.ParseTraceRanges(json);
        Assert.Equal(2, ranges.Count);
        Assert.All(ranges, r => Assert.Equal("agent-trace", r.Provenance.Source));

        var hunk = new DiffHunk { NewStart = 12, NewCount = 4 };
        var joined = ProvenanceReader.ForHunk(ranges, "src/Auth.cs", hunk);
        Assert.NotNull(joined);
        Assert.Equal("Loom-3", joined!.Agent);
        Assert.Equal("c0ffee", joined.Sha);
    }

    [Fact]
    public void AgentTrace_ExternalVendorShape_IsTolerated()
    {
        // A vendor trace: top-level array, session-level author, alternate range keys.
        var json = """
        [
          { "path": "app/Main.cs", "range": { "start": 3, "end": 9 }, "author": "cursor-agent", "ticket": "JIRA-1" }
        ]
        """;

        var ranges = ProvenanceReader.ParseTraceRanges(json);
        Assert.Single(ranges);
        Assert.Equal("cursor-agent", ranges[0].Provenance.Agent);
        Assert.Equal("JIRA-1", ranges[0].Provenance.Task);

        var joined = ProvenanceReader.ForHunk(ranges, "app/Main.cs", new DiffHunk { NewStart = 5, NewCount = 1 });
        Assert.Equal("cursor-agent", joined!.Agent);
    }

    [Fact]
    public void AgentTrace_Malformed_ReturnsEmpty_NeverThrows()
    {
        Assert.Empty(ProvenanceReader.ParseTraceRanges("{ not json"));
        Assert.Empty(ProvenanceReader.ParseTraceRanges(""));
        Assert.Empty(ProvenanceReader.FromAgentTrace("[ { \"no\": \"file\" } ]"));
    }

    [Fact]
    public void ForHunk_NoOverlap_ReturnsNull()
    {
        var ranges = ProvenanceReader.ParseTraceRanges("""{ "entries": [ { "file": "a.cs", "startLine": 1, "endLine": 3, "agent": "x" } ] }""");
        Assert.Null(ProvenanceReader.ForHunk(ranges, "a.cs", new DiffHunk { NewStart = 50, NewCount = 2 }));
    }

    // ---- Emitter write side round-trips through the reader --------------------

    [Fact]
    public void Emitter_SerializedTrace_RoundTripsThroughReader()
    {
        var session = new AgentTraceSession("Loom-3", "P2-11", "plan-7", "sess-1", "deadbeef", new List<AgentTraceContribution>
        {
            new("src/Auth.cs", 10, 20),
        });

        var json = AgentTraceEmitter.SerializeTrace(session);
        var ranges = ProvenanceReader.ParseTraceRanges(json);

        Assert.Single(ranges);
        Assert.Equal("Loom-3", ranges[0].Provenance.Agent);
        Assert.Equal("P2-11", ranges[0].Provenance.Task);
        Assert.Equal("plan-7", ranges[0].Provenance.Plan);
        Assert.Equal("deadbeef", ranges[0].Provenance.Sha);
        Assert.Equal(10, ranges[0].StartLine);
    }

    [Fact]
    public void Emitter_BuildTrailers_AppendsAndIsIdempotent()
    {
        var once = AgentTraceEmitter.BuildTrailers("Fix bug", "Loom-3", "P2-11", "plan-7");
        Assert.Contains("Agent: Loom-3", once);
        Assert.Contains("Task: P2-11", once);
        Assert.Contains("Plan: plan-7", once);

        // Re-applying does not duplicate an existing trailer.
        var twice = AgentTraceEmitter.BuildTrailers(once, "Loom-3", "P2-11", "plan-7");
        Assert.Equal(once, twice);

        var read = ProvenanceReader.FromTrailers(once, "sha");
        Assert.Equal("Loom-3", read!.Agent);
    }
}
