using System;
using System.Text.RegularExpressions;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The pure "what host did this agent get blocked on?" detector — the reusable core of the egress
/// block-notification fallback (Fix 2). Given a CLI's failure output (its terminal tail / death
/// reason) it extracts the egress host the default-deny proxy refused, so the surface can prompt
/// "Agent X couldn't reach HOST — unblock it or keep it blocked?".
///
/// <para>It is intentionally engine-free and side-effect-free so BOTH callers share one rule: the App
/// (running it on a dead agent's terminal replay) and a future daemon-side proxy-denial reader. A host
/// already on the allowlist is never a block (a transient failure to a permitted host is not a policy
/// denial), and a git host is never surfaced (A6 — git egress is the daemon read-only git proxy's job,
/// never the agent's own route, so we never invite the user to re-open it here).</para>
/// </summary>
public static class EgressBlockDetector
{
    // Case-insensitive, host-bearing failure phrases a default-deny proxy / pinned DNS produces when it
    // refuses egress. A hostname is at least one dotted label of letters/digits/hyphens ending in a TLD.
    private const string Host = @"(?<host>[a-z0-9](?:[a-z0-9-]*[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]*[a-z0-9])?)+)";

    private static readonly Regex[] Patterns =
    {
        // claude-code: "Failed to connect to platform.claude.com: ERR_SOCKET_CLOSED"; also curl/node forms.
        new($@"(?:failed to connect to|unable to connect to|error connecting to|connect(?:ing)? to)\s+{Host}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // DNS pinned to allowlist-only → NXDOMAIN: "getaddrinfo ENOTFOUND host", "EAI_AGAIN host".
        new($@"getaddrinfo\s+(?:enotfound|eai_again)\s+{Host}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // curl / git: "Could not resolve host: host".
        new($@"could not resolve host:?\s*{Host}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        // "host:443 ECONNREFUSED" / "connection to host refused" / "host ... ETIMEDOUT".
        // "etimeout" is claude-code's own (non-node) spelling on its startup-connectivity screen.
        new($@"{Host}(?::\d+)?\b[^\n]*?(?:econnrefused|etimedout|etimeout|err_socket_closed|connection refused|network is unreachable)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
    };

    /// <summary>
    /// The egress host <paramref name="failureOutput"/> shows the agent could not reach, or null when
    /// there is no blocked-host signal. <paramref name="isAllowed"/> (the live allowlist check) filters
    /// out hosts already permitted — a failure to an allowed host is a transient network issue, not a
    /// policy block, so it never prompts. Git hosts are never returned (A6).
    /// </summary>
    public static string? TryDetectBlockedHost(string? failureOutput, Func<string, bool>? isAllowed = null)
    {
        if (string.IsNullOrWhiteSpace(failureOutput))
        {
            return null;
        }

        foreach (var pattern in Patterns)
        {
            foreach (Match m in pattern.Matches(failureOutput))
            {
                var host = m.Groups["host"].Value.Trim().Trim('.').ToLowerInvariant();
                if (host.Length == 0 || !host.Contains('.'))
                {
                    continue;
                }

                if (EgressAllowlistEntry.LooksLikeGitHost(host))
                {
                    continue; // A6 — never invite re-opening a git-host route
                }

                if (isAllowed is not null && isAllowed(host))
                {
                    continue; // already permitted → the failure isn't a policy block
                }

                return host;
            }
        }

        return null;
    }
}
