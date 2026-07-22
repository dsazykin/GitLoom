using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The pure egress block-detector (Fix 2 core): given a CLI's failure output, name the host the
/// default-deny proxy refused — the "what was blocked" behind the unblock/keep prompt. Shared by the
/// App (a dead agent's terminal replay) and a future daemon-side proxy-denial reader.
/// </summary>
public class EgressBlockDetectorTests
{
    [Fact]
    public void DetectsTheHost_ClaudeCode_StartupConnectivityFailure()
    {
        // The exact field failure that started this: claude-code's startup check to platform.claude.com
        // is refused by the default-deny proxy, so it prints this and exits 1.
        const string tail = "Welcome to Claude Code v2.1.210\nUnable to connect to Anthropic services\n"
            + "Failed to connect to platform.claude.com: ERR_SOCKET_CLOSED\nPlease check your internet connection.";
        Assert.Equal("platform.claude.com", EgressBlockDetector.TryDetectBlockedHost(tail));
    }

    [Theory]
    [InlineData("getaddrinfo ENOTFOUND statsig.anthropic.com", "statsig.anthropic.com")]
    [InlineData("curl: (6) Could not resolve host: auth.openai.com", "auth.openai.com")]
    [InlineData("api.example.com:443: connection refused", "api.example.com")]
    [InlineData("Error connecting to telemetry.vendor.io", "telemetry.vendor.io")]
    // The cleaned form of claude-code's real Ink death screen (cursor-column moves → spaces), with
    // its own "ETIMEOUT" spelling — the whole line as TailText now renders it.
    [InlineData("Unable to connect to Anthropic services Failed to connect to platform.claude.com: ETIMEOUT",
        "platform.claude.com")]
    [InlineData("platform.claude.com:443 ETIMEOUT", "platform.claude.com")]
    public void DetectsTheHost_AcrossCommonFailureForms(string tail, string expected)
    {
        Assert.Equal(expected, EgressBlockDetector.TryDetectBlockedHost(tail));
    }

    [Fact]
    public void ReturnsNull_WhenNoConnectionFailureSignal()
    {
        Assert.Null(EgressBlockDetector.TryDetectBlockedHost("CLI exited (0). all good"));
        Assert.Null(EgressBlockDetector.TryDetectBlockedHost(""));
        Assert.Null(EgressBlockDetector.TryDetectBlockedHost(null));
    }

    [Fact]
    public void IgnoresHosts_AlreadyOnTheAllowlist()
    {
        // A failure to a permitted host is a transient network issue, not a policy block — no prompt.
        Assert.Null(EgressBlockDetector.TryDetectBlockedHost(
            "Failed to connect to api.anthropic.com: ETIMEDOUT", isAllowed: h => h == "api.anthropic.com"));

        // …but a DIFFERENT, non-allowed host is still surfaced.
        Assert.Equal("platform.claude.com", EgressBlockDetector.TryDetectBlockedHost(
            "Failed to connect to platform.claude.com: ERR_SOCKET_CLOSED", isAllowed: h => h == "api.anthropic.com"));
    }

    [Fact]
    public void NeverSurfacesAGitHost_A6()
    {
        // Even if a git host appears in a failure line, we never invite the user to re-open it (A6 —
        // git egress is the daemon read-only git proxy's job, never the agent's own route).
        Assert.Null(EgressBlockDetector.TryDetectBlockedHost("Failed to connect to github.com: ECONNREFUSED"));
    }
}
