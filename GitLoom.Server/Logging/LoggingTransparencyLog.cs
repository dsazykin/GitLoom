using System;
using System.Collections.Generic;
using GitLoom.Core.Agents.Sandbox;
using Microsoft.Extensions.Logging;

namespace GitLoom.Server.Logging;

/// <summary>
/// Decorates the P2-17 <see cref="INetworkTransparencyLog"/> sink and tees each recorded verdict into
/// the <c>Egress</c> daemon-log category, so an operator can watch what an agent reached in
/// <c>egress.log</c> / the journal without waiting for the full P2-17 transparency UI. Forwards every
/// call to the wrapped sink unchanged (the real transparency feed is authoritative); the log line is
/// the diagnostic complement.
///
/// <para><b>Schema stability (P2-17 / P2-44):</b> the line carries only host + verdict + kind + agent +
/// bytes — a summary, never a request body or secret — and later network-transparency / sandbox-health
/// panels read this shape, so keep it stable.</para>
/// </summary>
public sealed class LoggingTransparencyLog : INetworkTransparencyLog
{
    private readonly INetworkTransparencyLog _inner;
    private readonly ILogger _log;

    public LoggingTransparencyLog(INetworkTransparencyLog inner, ILoggerFactory loggerFactory)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Egress);
    }

    public void Record(TransparencyLine line)
    {
        _inner.Record(line);

        if (line is null)
            return;

        // A denial is the operationally interesting event (an agent tried to reach a blocked host);
        // an allowed fetch is Information. Summary fields only — never the request body.
        var denied = string.Equals(line.Verdict, "Denied", StringComparison.OrdinalIgnoreCase);
        _log.Log(
            denied ? LogLevel.Warning : LogLevel.Information,
            "egress {Verdict} host={Host} kind={Kind} agent={Agent} bytes={Bytes}",
            line.Verdict, line.Host, line.Kind, line.AgentId, line.Bytes);
    }

    public IReadOnlyList<TransparencyLine> Lines => _inner.Lines;
}
