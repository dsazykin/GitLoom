using System;
using System.Collections.Concurrent;

namespace Mainguard.Server.Runtime;

/// <summary>
/// A memory-only cache of the model API keys the client has handed this daemon session, keyed by
/// agent kind. The BYOK keystore is host-side (P2-01 — the daemon has no keyring), so keys only ever
/// arrive on <c>SpawnAgent</c>; a coordinator-initiated worker spawn (the in-jail IPC channel) has
/// no client in the loop and reuses the key last supplied for that kind. Never persisted, never
/// logged (the value is write-only until consumed by the sandbox secret injector), gone with the
/// daemon process.
/// </summary>
public sealed class SessionKeyCache
{
    private readonly ConcurrentDictionary<string, string> _byKind = new(StringComparer.Ordinal);

    /// <summary>Records the key supplied for <paramref name="agentKind"/> (empty values are ignored).</summary>
    public void Remember(string agentKind, string? modelApiKey)
    {
        if (!string.IsNullOrWhiteSpace(agentKind) && !string.IsNullOrWhiteSpace(modelApiKey))
        {
            _byKind[agentKind] = modelApiKey;
        }
    }

    /// <summary>The last key supplied for <paramref name="agentKind"/>, or null when none was.</summary>
    public string? TryGet(string agentKind) =>
        agentKind is not null && _byKind.TryGetValue(agentKind, out var key) ? key : null;
}
