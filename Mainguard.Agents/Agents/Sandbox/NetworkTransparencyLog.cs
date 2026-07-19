using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// One network-transparency line (P2-17): every proxied fetch and every egress verdict is recorded
/// here so the user can see exactly what an agent reached. Distinct from the tamper-evident audit log
/// (G-17): transparency is the human-visible "what happened on the wire" feed; audit is the
/// security-decision record. A denied request appears in <b>both</b>.
/// </summary>
public sealed record TransparencyLine(
    string Kind,
    string Host,
    string Detail,
    string AgentId,
    long Bytes,
    string Verdict,
    DateTimeOffset When)
{
    public static TransparencyLine Now(string kind, string host, string detail, string agentId, long bytes, string verdict)
        => new(kind, host, detail, agentId, bytes, verdict, DateTimeOffset.UtcNow);

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture,
            "{0:O} [{1}] {2} {3} agent={4} bytes={5} => {6}", When, Kind, Host, Detail, AgentId, Bytes, Verdict);
}

/// <summary>The P2-17 transparency sink seam. P2-17 supplies the persisted/streamed implementation.</summary>
public interface INetworkTransparencyLog
{
    void Record(TransparencyLine line);
    IReadOnlyList<TransparencyLine> Lines { get; }
}

/// <summary>A thread-safe in-memory transparency log — the pre-P2-17 sink, used by the daemon and tests.</summary>
public sealed class InMemoryNetworkTransparencyLog : INetworkTransparencyLog
{
    private readonly List<TransparencyLine> _lines = new();
    private readonly object _gate = new();

    public void Record(TransparencyLine line)
    {
        lock (_gate) { _lines.Add(line); }
    }

    public IReadOnlyList<TransparencyLine> Lines
    {
        get { lock (_gate) { return _lines.ToArray(); } }
    }
}
