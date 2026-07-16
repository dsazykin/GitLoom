using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.App.Services;

/// <summary>One installable-and-installed agent CLI as the coordinator picker shows it.</summary>
/// <param name="Id">The adapter id / agentKind (e.g. <c>claude-code</c>).</param>
/// <param name="Version">The pinned, installed version.</param>
/// <param name="ApiKeyEnvVar">The env-var NAME the CLI reads its key from; empty = interactive
/// login only (never a key value).</param>
public sealed record InstalledCliOption(string Id, string Version, string ApiKeyEnvVar)
{
    /// <summary>The picker's display line: id + version, honest about the login mode.</summary>
    public string Display => $"{Id} {Version}";
}

/// <summary>
/// The control center's seam onto the daemon's CLI-agent capabilities (start the coordinator CLI,
/// enumerate installed CLIs). Implemented by <see cref="DaemonBackedOrchestrator"/>; a fake stands
/// in for VM tests — the ViewModel never touches gRPC types (G-18).
/// </summary>
public interface ICliAgentHost
{
    /// <summary>The CLIs installed in the VM (empty when none / daemon unreachable throws).</summary>
    Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct);

    /// <summary>
    /// Spawns the coordinator CLI (role <c>coordinator</c>) against the active repo and returns its
    /// agent id. Throws with an honest, user-renderable message when no repo is active or the
    /// daemon refuses.
    /// </summary>
    Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct);

    /// <summary>The live coordinator's agent id, or null when none is running (from the agent stream).</summary>
    string? CoordinatorAgentId { get; }
}

/// <summary>
/// Maps an adapter's <c>apiKeyEnvVar</c> (the CLI's own env contract, e.g. <c>ANTHROPIC_API_KEY</c>)
/// to the P2-01 BYOK keystore provider id (<c>llm_&lt;provider&gt;</c> keys — see
/// <c>ApiKeySettingsViewModel</c>). Pure; null = no keystore provider maps, the CLI logs in
/// interactively inside its terminal.
/// </summary>
public static class ApiKeyProviderMap
{
    public static string? ProviderForEnvVar(string? apiKeyEnvVar) => apiKeyEnvVar switch
    {
        "ANTHROPIC_API_KEY" => "anthropic",
        "OPENAI_API_KEY" => "openai",
        _ => null,
    };

    /// <summary>The keystore key name for a provider (the ApiKeySettings convention).</summary>
    public static string KeystoreKeyFor(string provider) => $"llm_{provider}";
}
