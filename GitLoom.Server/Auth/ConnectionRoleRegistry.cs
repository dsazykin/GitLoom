using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace GitLoom.Server.Auth;

/// <summary>The credential class a connection authenticated as (P2-14 role enforcement).</summary>
public enum ConnectionRole
{
    /// <summary>The human operator (the primary session token). Full surface.</summary>
    Operator,

    /// <summary>A coordinator chat agent. Chat + capped tools only — no merge power, no plan approval.</summary>
    Coordinator,
}

/// <summary>
/// Maps a presented bearer token to its <see cref="ConnectionRole"/> (P2-14). The primary
/// <see cref="SessionTokenFile"/> token is the <see cref="ConnectionRole.Operator"/>; the daemon issues a
/// distinct token per coordinator agent, registered here as <see cref="ConnectionRole.Coordinator"/>. The
/// role is <b>bound to the token</b>, not client-asserted — so a coordinator cannot escape its role by
/// setting a header, and the <see cref="RoleInterceptor"/> denies it merge/approval RPCs (test 6).
/// </summary>
public sealed class ConnectionRoleRegistry
{
    private readonly ConcurrentDictionary<string, byte> _coordinatorTokens = new(StringComparer.Ordinal);

    /// <summary>Issues + registers a fresh coordinator token (64 hex chars, like the session token).</summary>
    public string IssueCoordinatorToken()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _coordinatorTokens[token] = 0;
        return token;
    }

    /// <summary>Registers a known token as a coordinator credential (tests supply a fixed one).</summary>
    public void RegisterCoordinatorToken(string token)
    {
        if (!string.IsNullOrWhiteSpace(token))
        {
            _coordinatorTokens[token] = 0;
        }
    }

    /// <summary>Resolves the role for a presented bearer token (unknown/primary → <see cref="ConnectionRole.Operator"/>).</summary>
    public ConnectionRole Resolve(string? bearerToken) =>
        bearerToken is not null && _coordinatorTokens.ContainsKey(bearerToken)
            ? ConnectionRole.Coordinator
            : ConnectionRole.Operator;
}
