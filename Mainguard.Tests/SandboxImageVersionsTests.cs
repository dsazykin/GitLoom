using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The version-anchor lookup + the agent-image override accessor. <see cref="SandboxImageVersions.For"/>
/// keys on the UNTAGGED name so a <c>MAINGUARD_AGENT_IMAGE</c> tag override still version-checks; the
/// launcher and the provisioner both resolve the tag through <see cref="SandboxImageVersions.AgentBaseRef()"/>
/// so they never skew (the app builds/labels exactly what the daemon preflights).
/// </summary>
public class SandboxImageVersionsTests
{
    [Fact]
    public void For_KeysOnUntaggedName_SurvivesTagOverride()
    {
        Assert.Equal(SandboxImageVersions.AgentBase, SandboxImageVersions.For("mainguard-agent-base:latest"));
        Assert.Equal(SandboxImageVersions.AgentBase, SandboxImageVersions.For("mainguard-agent-base:dev"));
        Assert.Equal(SandboxImageVersions.AgentBase, SandboxImageVersions.For("mainguard-agent-base"));
        Assert.Equal(SandboxImageVersions.EgressProxy, SandboxImageVersions.For("mainguard-egress-proxy:latest"));
    }

    [Fact]
    public void For_UnknownImage_IsNull_PresenceOnly()
    {
        // A fully-renamed override we never built has no expected hash — the preflight/probe falls
        // back to a presence-only check for it rather than flagging it stale.
        Assert.Null(SandboxImageVersions.For("some/other-image:latest"));
        Assert.Null(SandboxImageVersions.For("registry:5000/unrelated"));
    }

    [Theory]
    [InlineData("mainguard-agent-base:latest", "mainguard-agent-base")]
    [InlineData("mainguard-agent-base", "mainguard-agent-base")]
    [InlineData("registry:5000/mainguard-agent-base:dev", "registry:5000/mainguard-agent-base")]
    [InlineData("registry:5000/mainguard-agent-base", "registry:5000/mainguard-agent-base")]
    [InlineData("mainguard-agent-base@sha256:abcdef", "mainguard-agent-base")]
    public void UntaggedName_StripsTagAndDigest_PreservesRegistryPort(string input, string expected)
    {
        Assert.Equal(expected, SandboxImageVersions.UntaggedName(input));
    }

    [Theory]
    [InlineData(null, "mainguard-agent-base:latest")]
    [InlineData("", "mainguard-agent-base:latest")]
    [InlineData("mainguard-agent-base:dev", "mainguard-agent-base:dev")]
    [InlineData("myregistry/mainguard-agent-base:pinned", "myregistry/mainguard-agent-base:pinned")]
    public void AgentBaseRef_DefaultsToLatest_HonorsOverride(string? envOverride, string expected)
    {
        Assert.Equal(expected, SandboxImageVersions.AgentBaseRef(envOverride));
    }
}
