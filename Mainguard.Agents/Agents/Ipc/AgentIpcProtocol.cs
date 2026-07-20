using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mainguard.Agents.Agents.Ipc;

/// <summary>
/// The fixed layout of the per-coordinator agent-IPC directory: a daemon-owned ext4 dir on the VM,
/// bind-mounted READ-ONLY into the coordinator's jail at <see cref="SandboxMount"/>. It carries
/// exactly two entries — the daemon-served Unix socket and the <c>mainguard-agent</c> spawn shim the
/// launch wrapper puts on PATH. Only a session spawned with the <c>coordinator</c> role gets this
/// mount; workers have no spawn channel (least privilege). Connecting to a Unix socket is not a
/// filesystem write, so the read-only mount is sufficient — the jail can talk, but can never swap
/// the shim or the socket for another agent's.
/// </summary>
public static class AgentIpcPaths
{
    /// <summary>Where the coordinator's IPC dir appears inside its jail.</summary>
    public const string SandboxMount = "/opt/mainguard/ipc";

    /// <summary>The daemon-served Unix socket file name (inside the IPC dir).</summary>
    public const string SocketFileName = "daemon.sock";

    /// <summary>The executable spawn shim's file name (inside the IPC dir; on the wrapper's PATH).</summary>
    public const string ShimFileName = "mainguard-agent";

    /// <summary>The socket path as the jail sees it (what the shim dials by default).</summary>
    public const string SandboxSocketPath = SandboxMount + "/" + SocketFileName;
}

/// <summary>One line-delimited JSON request from the in-jail <c>mainguard-agent</c> shim.</summary>
public sealed record AgentIpcRequest(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("agentKind")] string? AgentKind = null,
    [property: JsonPropertyName("taskPrompt")] string? TaskPrompt = null)
{
    public const string SpawnOp = "spawn";
    public const string ListOp = "list";
}

/// <summary>One line-delimited JSON response toward the shim. Errors are honest prose, no stacks.</summary>
public sealed record AgentIpcResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("agentId")] string? AgentId = null,
    [property: JsonPropertyName("agents")] string[]? Agents = null,
    [property: JsonPropertyName("error")] string? Error = null);

/// <summary>
/// The pure wire codec for the coordinator→daemon spawn channel: newline-delimited JSON, one
/// request line → one response line. Malformed input is a typed null (the server answers with an
/// error response), never an exception escaping to the socket loop.
/// </summary>
public static class AgentIpcProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Parses one request line; null when the line is not a valid request.</summary>
    public static AgentIpcRequest? TryParseRequest(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<AgentIpcRequest>(line, Options);
            return request is { Op.Length: > 0 } ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Serializes one response as a single line (no embedded newlines).</summary>
    public static string SerializeResponse(AgentIpcResponse response) =>
        JsonSerializer.Serialize(response, Options);

    /// <summary>Serializes one request as a single line (client/shim side; used by tests).</summary>
    public static string SerializeRequest(AgentIpcRequest request) =>
        JsonSerializer.Serialize(request, Options);
}
