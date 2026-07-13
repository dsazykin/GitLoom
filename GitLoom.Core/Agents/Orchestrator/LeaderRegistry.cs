using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GitLoom.Core.Agents.Orchestrator;

/// <summary>
/// One durable PTY-session record owned by the <see cref="SessionLeader"/>. The socket path is the
/// leader's per-agent Unix socket the daemon reattaches over. This is <b>leader-owned state</b>: the
/// daemon only reads it on boot to reattach — there are no daemon-side pidfiles (contract §2.3).
/// </summary>
public sealed record LeaderSession(
    string AgentId,
    string RepoHash,
    string ContainerId,
    int Cols,
    int Rows,
    string SocketPath);

/// <summary>
/// The persistent, leader-owned registry of live PTY sessions (a small JSON file the leader process
/// rewrites on every mutation). It survives a daemon <c>kill -9</c> because the leader — not the daemon
/// — owns it; on boot the daemon reads it to reattach streams. Writes are atomic (temp-then-rename) so
/// a crash mid-write never corrupts the registry.
/// </summary>
public sealed class LeaderRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly string _path;
    private readonly object _gate = new();

    public LeaderRegistry(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The backing file path (leader-owned).</summary>
    public string Path => _path;

    /// <summary>Reads every recorded session (empty when the registry file is absent or unreadable).</summary>
    public IReadOnlyList<LeaderSession> Load()
    {
        lock (_gate)
        {
            return LoadLocked();
        }
    }

    /// <summary>Adds or replaces a session (keyed by agent id) and persists.</summary>
    public void Upsert(LeaderSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            var rows = LoadLocked().Where(s => !AgentEq(s.AgentId, session.AgentId)).ToList();
            rows.Add(session);
            SaveLocked(rows);
        }
    }

    /// <summary>Removes a session by agent id and persists (idempotent).</summary>
    public void Remove(string agentId)
    {
        lock (_gate)
        {
            var rows = LoadLocked().Where(s => !AgentEq(s.AgentId, agentId)).ToList();
            SaveLocked(rows);
        }
    }

    /// <summary>Replaces the whole registry (used by the boot reattach reconcile).</summary>
    public void Save(IReadOnlyList<LeaderSession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        lock (_gate)
        {
            SaveLocked(sessions.ToList());
        }
    }

    private IReadOnlyList<LeaderSession> LoadLocked()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<LeaderSession>();
            }

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<LeaderSession>();
            }

            return JsonSerializer.Deserialize<List<LeaderSession>>(json, JsonOptions)
                ?? (IReadOnlyList<LeaderSession>)Array.Empty<LeaderSession>();
        }
        catch (Exception)
        {
            // A corrupt/half-written registry reconciles toward Docker truth on boot anyway; never throw here.
            return Array.Empty<LeaderSession>();
        }
    }

    private void SaveLocked(List<LeaderSession> rows)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(rows, JsonOptions);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _path, overwrite: true);
    }

    private static bool AgentEq(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);
}
