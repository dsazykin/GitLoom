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

    private volatile System.Collections.Generic.IReadOnlyDictionary<string, string>? _extraEnv;

    /// <summary>Records the custom env-var keys supplied on a client spawn (llm_env_* — they are
    /// global, not per-kind), so a coordinator-initiated worker spawn injects them too.</summary>
    public void RememberExtraEnv(System.Collections.Generic.IReadOnlyDictionary<string, string>? extraEnv)
    {
        if (extraEnv is { Count: > 0 })
        {
            _extraEnv = extraEnv;
        }
    }

    /// <summary>The custom env-var keys last supplied by a client spawn, or null.</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? TryGetExtraEnv() => _extraEnv;

    private readonly ConcurrentDictionary<string, System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>>
        _cliCredentialsByKind = new(StringComparer.Ordinal);

    /// <summary>Records the CLI login-state files the client restored on a spawn of
    /// <paramref name="agentKind"/>, so a coordinator-initiated worker of the same kind (no client
    /// in the loop) boots logged in too. Memory-only, same lifetime rules as the model keys — the
    /// durable store is the host OS keychain, never the daemon.</summary>
    public void RememberCliCredentials(
        string agentKind, System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? files)
    {
        if (!string.IsNullOrWhiteSpace(agentKind) && files is { Count: > 0 })
        {
            _cliCredentialsByKind[agentKind] = files;
        }
    }

    /// <summary>The CLI login-state files last supplied for <paramref name="agentKind"/>, or null.</summary>
    public System.Collections.Generic.IReadOnlyList<Mainguard.Agents.Agents.Sandbox.SandboxCredentialFile>? TryGetCliCredentials(string agentKind) =>
        agentKind is not null && _cliCredentialsByKind.TryGetValue(agentKind, out var files) ? files : null;
}
