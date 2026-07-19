using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Ipc;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Mainguard.Server.Auth;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;

namespace Mainguard.Server.Runtime;

/// <summary>A spawn the daemon refuses on policy (kill switch engaged, no repo, …) — not a fault.</summary>
public sealed class AgentSpawnRefusedException : Exception
{
    public AgentSpawnRefusedException(string message) : base(message)
    {
    }
}

/// <summary>
/// The ONE spawn/stop workflow behind both entry points — <c>AgentService.SpawnAgent</c> (the
/// operator/UI path) and the coordinator's in-jail <c>gitloom-agent</c> shim (the
/// <see cref="CoordinatorIpcServer"/> path) — so a coordinator-spawned worker takes exactly the
/// same chain as an RPC spawn: session record → (coordinator only: IPC endpoint) → worktree +
/// hardened jail (<see cref="SandboxAgentLauncher"/>) → CLI under a real TTY
/// (<see cref="AgentCliBinder"/>) → (managed only: terminal input lock, P2-14). Kept out of the
/// gRPC class per the P2-02 rejection trigger (no business logic in transports).
/// </summary>
public sealed class AgentSpawnService
{
    private readonly AgentSessionStore _store;
    private readonly SandboxAgentLauncher _launcher;
    private readonly AgentCliBinder _binder;
    private readonly CoordinatorIpcServer _ipc;
    private readonly SessionKeyCache _keys;
    private readonly TerminalLockRegistry _locks;
    private readonly KillSwitchGate _killGate;
    private readonly IAuditLog _audit;
    private readonly ILogger _spawnLog;
    private readonly ILogger _coordLog;

    public AgentSpawnService(
        AgentSessionStore store,
        SandboxAgentLauncher launcher,
        AgentCliBinder binder,
        CoordinatorIpcServer ipc,
        SessionKeyCache keys,
        TerminalLockRegistry locks,
        KillSwitchGate killGate,
        IAuditLog audit,
        ILoggerFactory loggerFactory)
    {
        _store = store;
        _launcher = launcher;
        _binder = binder;
        _ipc = ipc;
        _keys = keys;
        _locks = locks;
        _killGate = killGate;
        _audit = audit;
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _spawnLog = loggerFactory.CreateLogger(DaemonLogCategories.Spawn);
        _coordLog = loggerFactory.CreateLogger(DaemonLogCategories.Coordinator);
    }

    /// <summary>
    /// Spawns one agent. Throws <see cref="ArgumentException"/> on a missing kind,
    /// <see cref="AgentSpawnRefusedException"/> on a policy refusal (kill switch), and lets the
    /// launcher's typed provisioning failures propagate (the callers map them). Returns the agent id.
    /// </summary>
    public async Task<string> SpawnAsync(
        string repoHandle, string agentKind, string? modelApiKey, string role, CancellationToken ct)
    {
        // SA-1/F4: spawns are refused while the kill switch holds everything frozen — the IPC path
        // included (a frozen coordinator must not be able to fan out workers).
        if (_killGate.IsFrozen)
        {
            _spawnLog.LogWarning("spawn refused: kill switch engaged (kind={Kind})", agentKind);
            throw new AgentSpawnRefusedException(
                "Everything is frozen (kill switch engaged) — spawns are refused. Resume first.");
        }

        if (string.IsNullOrWhiteSpace(agentKind))
        {
            _spawnLog.LogWarning("spawn refused: agent_kind is required");
            throw new ArgumentException("agent_kind is required.");
        }

        // Memory-only, per-kind: lets a coordinator-initiated worker of the same kind reuse the key
        // the client last supplied (the daemon has no keystore of its own — P2-01 is host-side).
        _keys.Remember(agentKind, modelApiKey);

        // Record the session first (its id names the worktree + container), then run the real
        // P2-06/P2-07 spawn chain. A provisioned repo takes the real-jail path; an unprovisioned
        // handle degrades to a session-only record (no fabricated jail).
        var session = _store.Spawn(agentKind, role);

        // Correlation: every Spawn/Egress/Terminal line for this agent shares its id — the scope
        // renders as (agentId) in the file format, so one grep follows the whole chain.
        using var scope = _spawnLog.BeginScope(session.Id);
        _spawnLog.LogInformation("spawn: session created role={Role} kind={Kind}", role, agentKind);
        string? ipcDir = null;
        try
        {
            if (role == AgentRoles.Coordinator)
            {
                // The endpoint is a container mount source, so it must exist before the jail does.
                // Best-effort: a box where the Unix socket cannot bind still gets a working
                // coordinator (terminal + jail), just without the in-jail spawn tool — audited.
                try
                {
                    ipcDir = _ipc.CreateEndpoint(session.Id, HandleShimRequestAsync);
                    _coordLog.LogInformation("coordinator IPC endpoint created: {Dir}", ipcDir);
                }
                catch (Exception ex)
                {
                    _coordLog.LogWarning(ex, "coordinator IPC endpoint failed — degrading to no spawn-shim");
                    _audit.Append(new AuditEvent("ipc_endpoint_failed", new Dictionary<string, string>
                    {
                        ["agent_id"] = session.Id,
                        ["reason"] = ex.Message,
                    }));
                }
            }

            var launch = await _launcher.TryLaunchAsync(
                repoHandle, session.Id, agentKind, modelApiKey, ipcDir, ct).ConfigureAwait(false);
            if (launch is not null)
            {
                _store.AttachSandbox(session.Id, launch.ContainerId, repoHandle);
                if (launch.LaunchCommand is { Count: > 0 })
                {
                    // The core P2-47→P2-03/09 wiring: the CLI starts inside the jail on a real TTY
                    // and TerminalService.Attach streams it (no more echo fallback for real agents).
                    _binder.TryBind(new AgentCliLaunchSpec(
                        session.Id, repoHandle, launch.ContainerId, launch.LaunchCommand));
                }
            }

            if (role == AgentRoles.Managed)
            {
                // P2-14: a managed worker's terminal is read-only — daemon-enforced, never UI-only.
                _locks.Lock(session.Id);
            }

            _spawnLog.LogInformation("spawn complete: jailed={Jailed}", launch is not null);
            return session.Id;
        }
        catch (Exception ex)
        {
            // Leave no residue on a failed spawn: endpoint, lock, and session record all go. Previously
            // a silent rethrow — now the failure is recorded before cleanup so the outage is diagnosable.
            _spawnLog.LogError(ex, "spawn failed — tearing down session/endpoint/lock");
            if (ipcDir is not null)
            {
                _ipc.CloseEndpoint(session.Id);
            }

            _locks.Unlock(session.Id);
            _store.Stop(session.Id);
            throw;
        }
    }

    /// <summary>Stops one agent: session record, CLI PTY, IPC endpoint, input lock, jail + worktree.</summary>
    public async Task<bool> StopAsync(string agentId, CancellationToken ct)
    {
        // Capture the session (with its container id/repo hash) BEFORE removing it, so a real jail +
        // worktree can be torn down after the record is gone.
        var session = _store.Find(agentId);
        var stopped = _store.Stop(agentId);

        _binder.Release(agentId);
        _ipc.CloseEndpoint(agentId);
        _locks.Unlock(agentId);

        if (stopped && session?.ContainerId is { Length: > 0 } containerId)
        {
            await _launcher.TeardownAsync(session.RepoHash, agentId, containerId, ct).ConfigureAwait(false);
        }

        _spawnLog.LogInformation("stop: agent={Agent} stopped={Stopped}", agentId, stopped);
        return stopped;
    }

    /// <summary>
    /// The coordinator shim's request handler. Identity is positional (only that coordinator's jail
    /// has the socket mount); the worker inherits the coordinator's repo and spawns MANAGED — same
    /// chain, locked terminal, visible in the activity bar as a subagent.
    /// </summary>
    internal async Task<AgentIpcResponse> HandleShimRequestAsync(
        AgentIpcRequest request, string coordinatorAgentId, CancellationToken ct)
    {
        _coordLog.LogInformation(
            "spawn-shim request: op={Op} kind={Kind} from coordinator={Coordinator}",
            request.Op, request.AgentKind, coordinatorAgentId);

        switch (request.Op)
        {
            case AgentIpcRequest.SpawnOp:
                if (string.IsNullOrWhiteSpace(request.AgentKind))
                {
                    return new AgentIpcResponse(Ok: false, Error: "an agent kind is required (gitloom-agent spawn <agent-kind>)");
                }

                var coordinator = _store.Find(coordinatorAgentId);
                if (coordinator is null)
                {
                    return new AgentIpcResponse(Ok: false, Error: "this coordinator session is no longer live");
                }

                if (coordinator.RepoHash is not { Length: > 0 } repoHandle)
                {
                    return new AgentIpcResponse(Ok: false, Error: "the coordinator has no provisioned repo to spawn against");
                }

                try
                {
                    var agentId = await SpawnAsync(
                        repoHandle, request.AgentKind, _keys.TryGet(request.AgentKind),
                        AgentRoles.Managed, ct).ConfigureAwait(false);
                    return new AgentIpcResponse(Ok: true, AgentId: agentId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return new AgentIpcResponse(Ok: false, Error: ex.Message);
                }

            case AgentIpcRequest.ListOp:
                var agents = _store.List()
                    .Select(s => $"{s.Id}\t{s.Kind}\t{s.State}\t{s.Role}")
                    .ToArray();
                return new AgentIpcResponse(Ok: true, Agents: agents);

            default:
                return new AgentIpcResponse(Ok: false, Error: $"unknown op '{request.Op}'");
        }
    }
}
