using Mainguard.Agents.Agents.Adapters;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Audit fix #13: the model API key must be injected under the env-var name the INSTALLED CLI
/// actually reads — a hardcoded <c>ANTHROPIC_API_KEY</c> for every agent kind meant codex/opencode
/// never saw their keys. The name rides the adapter's install marker; no marker keeps the legacy
/// fallback (dev boxes without a catalog), and a marker without a declared var injects nothing
/// (interactive-login CLIs).
/// </summary>
public sealed class SandboxSecretsMappingTests
{
    private static InstalledAdapterMarker Marker(string id, string? envVar) =>
        new(id, "1.0.0", new[] { $"/opt/gitloom/adapters/bin/{id}" }, envVar);

    [Fact]
    public void AdapterDeclaresEnvVar_KeyLandsUnderExactlyThatName()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets("sk-test-123", Marker("codex", "OPENAI_API_KEY"));

        Assert.Equal("sk-test-123", secrets.AgentEnv["OPENAI_API_KEY"]);
        Assert.DoesNotContain("ANTHROPIC_API_KEY", secrets.AgentEnv.Keys);
    }

    [Fact]
    public void AdapterDeclaresNoEnvVar_InteractiveLogin_NothingInjected()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets("sk-test-123", Marker("opencode", null));

        Assert.Empty(secrets.AgentEnv);
    }

    [Fact]
    public void NoMarkerAtAll_LegacyAnthropicFallback_KeepsDevFlowsWorking()
    {
        var secrets = SandboxAgentLauncher.BuildSecrets("sk-test-123", adapter: null);

        Assert.Equal("sk-test-123", secrets.AgentEnv["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void NoKeySupplied_NothingInjected_RegardlessOfAdapter()
    {
        Assert.Empty(SandboxAgentLauncher.BuildSecrets(null, Marker("claude-code", "ANTHROPIC_API_KEY")).AgentEnv);
        Assert.Empty(SandboxAgentLauncher.BuildSecrets("  ", adapter: null).AgentEnv);
    }

    [Fact]
    public void MarkerRoundTrip_CarriesTheEnvVar()
    {
        var json = InstalledAdapterMarker.Serialize(Marker("codex", "OPENAI_API_KEY"));
        var back = InstalledAdapterMarker.TryDeserialize(json);

        Assert.NotNull(back);
        Assert.Equal("OPENAI_API_KEY", back!.ApiKeyEnvVar);
    }

    [Fact]
    public void OlderMarkerWithoutTheField_StillDeserializes_AsInteractiveLogin()
    {
        // Markers written by a pre-fix installer have no apiKeyEnvVar — they must keep loading.
        var back = InstalledAdapterMarker.TryDeserialize(
            """{ "id": "claude-code", "version": "2.1.210", "launch": ["/opt/gitloom/adapters/bin/claude"] }""");

        Assert.NotNull(back);
        Assert.Null(back!.ApiKeyEnvVar);
    }
}
