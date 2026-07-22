using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Audit;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mainguard.Server.Runtime;

/// <summary>What the binder needs to start one CLI under a TTY inside a live jail.</summary>
public sealed record AgentCliLaunchSpec(
    string AgentId, string RepoHash, string ContainerId, IReadOnlyList<string> Launch);

/// <summary>
/// The complete, PTY-ready launch plan for one in-jail CLI — the exact command/argv/environment/size
/// the daemon-side PTY spawn consumes. Pure data, computed by <see cref="AgentCliBinder.BuildPtyLaunch"/>
/// so tests can assert the TTY-relevant bits (interactive <c>-i -t</c> exec, sane <c>TERM</c>,
/// positive dimensions) without spawning anything.
/// </summary>
public sealed record CliPtyLaunch(
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Environment,
    int Cols,
    int Rows);

/// <summary>
/// Binds a freshly launched jail's CLI to a real terminal: spawns the CLI inside the container
/// attached to a TTY (the default factory runs <c>docker exec -it</c> under a daemon-side forkpty
/// PTY — see <see cref="SandboxCliLaunch"/>), registers the long-lived session with
/// <see cref="TerminalSessionManager"/> (so <c>TerminalService.Attach</c> streams the REAL CLI) and
/// with the P2-09 <see cref="SessionLeader"/> (which owns per-agent PTY fds and their input pause),
/// and reflects the CLI's exit in the session store as a state delta.
///
/// <para>Binding is best-effort by design: on a box without the docker CLI (dev loop) the spawn
/// failure is audited and the agent degrades to the session-only shape (attach echoes) rather than
/// failing the whole spawn — the jail itself is real and correct either way.</para>
/// </summary>
public sealed class AgentCliBinder
{
    private const int AgentUid = 1000;
    private const int DefaultCols = 120;
    private const int DefaultRows = 32;

    private readonly TerminalSessionManager _terminals;
    private readonly SessionLeader _leader;
    private readonly AgentSessionStore _store;
    private readonly IAuditLog _audit;
    private readonly Func<AgentCliLaunchSpec, ITerminalSession> _sessionFactory;
    private readonly ILogger _log;

    public AgentCliBinder(
        TerminalSessionManager terminals,
        SessionLeader leader,
        AgentSessionStore store,
        IAuditLog audit,
        Func<AgentCliLaunchSpec, ITerminalSession>? sessionFactory = null,
        ILoggerFactory? loggerFactory = null)
    {
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _sessionFactory = sessionFactory ?? SpawnDockerExecPty;
        // Optional so the AgentCliWiringTests direct construction keeps working; DI supplies the real one.
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(DaemonLogCategories.Terminal);
    }

    /// <summary>
    /// The pure launch plan behind the production factory: <c>docker exec -i -t</c> (interactive
    /// TTY — <c>isatty()</c> true for the docker CLI daemon-side and for the agent CLI in-jail, so
    /// an unauthenticated CLI opens its interactive login instead of printing a non-interactive
    /// refusal and exiting), an explicit sane <c>TERM</c> on both sides of the exec, and a positive
    /// terminal size. The environment is minimal and secret-free (G-13) — the CLI's credentials come
    /// from the in-container <c>/run/secrets/agent.env</c> the launch wrapper sources.
    /// </summary>
    internal static CliPtyLaunch BuildPtyLaunch(AgentCliLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var (command, args) = SandboxCliLaunch.BuildDockerExecArgv(spec.ContainerId, spec.Launch, AgentUid);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = SandboxCliLaunch.InJailTerm,
        };
        foreach (var name in new[] { "PATH", "HOME", "DOCKER_HOST" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                env[name] = value;
            }
        }

        return new CliPtyLaunch(command, args, env, DefaultCols, DefaultRows);
    }

    /// <summary>The production factory: the <see cref="BuildPtyLaunch"/> plan under a real
    /// daemon-side PTY (<see cref="PtyProcessShim"/> forkpty/ConPTY).</summary>
    internal static ITerminalSession SpawnDockerExecPty(AgentCliLaunchSpec spec)
    {
        var launch = BuildPtyLaunch(spec);
        return PtyProcessShim.Spawn(
            launch.Command, launch.Args, Environment.CurrentDirectory, launch.Environment,
            launch.Cols, launch.Rows);
    }

    /// <summary>
    /// Starts the CLI and registers the bound session. Returns true when the terminal is live;
    /// false when the CLI could not be started (audited; the agent stays session-only + echo).
    /// </summary>
    public bool TryBind(AgentCliLaunchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        ITerminalSession session;
        try
        {
            session = _sessionFactory(spec);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "cli bind failed agent={Agent} — degrading to session-only echo", spec.AgentId);
            _audit.Append(new AuditEvent("cli_bind_failed", new Dictionary<string, string>
            {
                ["agent_id"] = spec.AgentId,
                ["agent_kind_launch"] = spec.Launch.Count > 0 ? spec.Launch[0] : string.Empty,
                ["reason"] = ex.Message,
            }));
            return false;
        }

        var bound = new BoundTerminalSession(spec.AgentId, session);
        _terminals.Bind(spec.AgentId, bound);
        _log.LogInformation("cli bound agent={Agent} container={Container}", spec.AgentId, spec.ContainerId);

        // P2-09: the leader owns the per-agent PTY (registry entry + kill + input pause seam).
        _leader.Register(
            new LeaderSession(spec.AgentId, spec.RepoHash, spec.ContainerId, DefaultCols, DefaultRows,
                SocketPath: string.Empty),
            kill: bound.Kill);

        _audit.Append(new AuditEvent("cli_bound", new Dictionary<string, string>
        {
            ["agent_id"] = spec.AgentId,
            ["container_id"] = spec.ContainerId,
        }));

        _ = WatchExitAsync(spec.AgentId, bound);
        return true;
    }

    /// <summary>Kills + unregisters the agent's CLI session (StopAgent / teardown). Idempotent.</summary>
    public void Release(string agentId)
    {
        _leader.Kill(agentId);
        _terminals.Release(agentId);
        _terminals.ClearBindPending(agentId); // torn down before it bound → no attach should keep waiting
    }

    /// <summary>Marks that a CLI bind is expected for <paramref name="agentId"/> (spawn in-flight) so an
    /// early attach waits for it instead of falling into echo — the attach-before-bind race. Cleared by a
    /// successful <see cref="TryBind"/> or by <see cref="ClearBindPending"/> on a session-only/failed spawn.</summary>
    public void MarkBindPending(string agentId) => _terminals.MarkBindPending(agentId);

    /// <summary>Clears the pending-bind flag (no CLI will bind for this agent — an attach should echo).</summary>
    public void ClearBindPending(string agentId) => _terminals.ClearBindPending(agentId);

    /// <summary>Cap on the last-output tail carried into the death reason/audit — enough to name
    /// the cause ("the input device is not a TTY", "Not logged in …", a stack-trace head).</summary>
    internal const int ExitTailChars = 400;

    private async Task WatchExitAsync(string agentId, BoundTerminalSession bound)
    {
        int exitCode;
        try
        {
            exitCode = await bound.ExitCode.ConfigureAwait(false);
        }
        catch (Exception)
        {
            exitCode = -1;
        }

        // Only reflect a natural CLI exit; a Release/Stop already removed the session record.
        if (_terminals.TryGetBound(agentId) is not null)
        {
            // The CLI's dying words (from the replay ring) are the diagnosis — a bare exit code
            // told the field NOTHING when the coordinator died at launch. They go to the audit
            // log durably; the bound session stays registered, so attaching to the dead agent's
            // terminal still replays the same output in full.
            var tail = bound.TailText(ExitTailChars);
            _log.LogInformation("cli exited agent={Agent} exitCode={ExitCode}", agentId, exitCode);
            _audit.Append(new AuditEvent("cli_exited", new Dictionary<string, string>
            {
                ["agent_id"] = agentId,
                ["exit_code"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["output_tail"] = tail,
            }));
            _store.MarkState(agentId, "Dead",
                tail.Length > 0 ? $"CLI exited ({exitCode}): {tail}" : $"CLI exited ({exitCode}).");
        }
    }
}
