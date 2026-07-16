using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Orchestrator;
using GitLoom.Core.Agents.Sandbox;
using GitLoom.Core.Audit;

namespace GitLoom.Server.Runtime;

/// <summary>What the binder needs to start one CLI under a TTY inside a live jail.</summary>
public sealed record AgentCliLaunchSpec(
    string AgentId, string RepoHash, string ContainerId, IReadOnlyList<string> Launch);

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

    public AgentCliBinder(
        TerminalSessionManager terminals,
        SessionLeader leader,
        AgentSessionStore store,
        IAuditLog audit,
        Func<AgentCliLaunchSpec, ITerminalSession>? sessionFactory = null)
    {
        _terminals = terminals ?? throw new ArgumentNullException(nameof(terminals));
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _sessionFactory = sessionFactory ?? SpawnDockerExecPty;
    }

    /// <summary>
    /// The production factory: <c>docker exec -it</c> under a real daemon-side PTY. The environment
    /// is minimal and secret-free (G-13) — the CLI's credentials come from the in-container
    /// <c>/run/secrets/agent.env</c> the launch wrapper sources.
    /// </summary>
    internal static ITerminalSession SpawnDockerExecPty(AgentCliLaunchSpec spec)
    {
        var (command, args) = SandboxCliLaunch.BuildDockerExecArgv(spec.ContainerId, spec.Launch, AgentUid);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = "xterm-256color",
        };
        foreach (var name in new[] { "PATH", "HOME", "DOCKER_HOST" })
        {
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
            {
                env[name] = value;
            }
        }

        return PtyProcessShim.Spawn(
            command, args, Environment.CurrentDirectory, env, DefaultCols, DefaultRows);
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
    }

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
            _store.MarkState(agentId, "Dead", $"CLI exited ({exitCode}).");
        }
    }
}
