using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Ipc;

namespace Mainguard.Server.Runtime;

/// <summary>Handles one shim request arriving on a coordinator's IPC socket.</summary>
public delegate Task<AgentIpcResponse> AgentIpcHandler(
    AgentIpcRequest request, string coordinatorAgentId, CancellationToken ct);

/// <summary>
/// The daemon side of the coordinator→daemon spawn channel: one Unix-domain socket per
/// coordinator, served from a daemon-owned ext4 dir that is bind-mounted READ-ONLY into that
/// coordinator's jail (<see cref="AgentIpcPaths.SandboxMount"/>). The dir also carries the
/// executable <c>mainguard-agent</c> shim (<see cref="AgentSpawnShim"/>) the launch wrapper puts on
/// PATH. The endpoint must exist BEFORE the container is created (it is a mount source), so
/// <see cref="CreateEndpoint"/> runs first in the spawn chain and <see cref="CloseEndpoint"/> is
/// part of teardown.
///
/// <para>Identity is positional: requests arriving on <c>&lt;agentId&gt;/daemon.sock</c> ARE that
/// coordinator's — only its jail has the mount. The protocol is one newline-delimited JSON request
/// per connection (<see cref="AgentIpcProtocol"/>); malformed input gets an honest error response,
/// never a dropped connection.</para>
/// </summary>
public sealed class CoordinatorIpcServer : IDisposable
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, Endpoint> _endpoints = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="root">The VM-side base dir for per-coordinator IPC dirs (ext4, daemon-owned).</param>
    public CoordinatorIpcServer(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("An IPC root directory is required.", nameof(root));
        }

        _root = root;
    }

    /// <summary>The per-coordinator IPC dir (the container mount source). The dir name is the
    /// agent id's 12-char prefix — Unix socket paths have a hard ~104-byte limit, and live-session
    /// prefix collisions are not a real risk.</summary>
    public string DirFor(string agentId) =>
        Path.Combine(_root, agentId.Length > 12 ? agentId[..12] : agentId);

    /// <summary>
    /// Materializes the coordinator's IPC dir (shim written 0755, socket bound + listening) and
    /// returns the dir path to bind-mount. Idempotent per agent id.
    /// </summary>
    public string CreateEndpoint(string agentId, AgentIpcHandler handler)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var endpoint = _endpoints.GetOrAdd(agentId, id => Endpoint.Start(DirFor(id), id, handler));
        return endpoint.Dir;
    }

    /// <summary>Stops the coordinator's listener and removes its IPC dir. Idempotent.</summary>
    public void CloseEndpoint(string agentId)
    {
        if (agentId is not null && _endpoints.TryRemove(agentId, out var endpoint))
        {
            endpoint.Dispose();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var agentId in _endpoints.Keys)
        {
            CloseEndpoint(agentId);
        }
    }

    private sealed class Endpoint : IDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _cts = new();

        public string Dir { get; }

        private Endpoint(string dir, Socket listener)
        {
            Dir = dir;
            _listener = listener;
        }

        public static Endpoint Start(string dir, string agentId, AgentIpcHandler handler)
        {
            Directory.CreateDirectory(dir);

            var shimPath = Path.Combine(dir, AgentIpcPaths.ShimFileName);
            File.WriteAllText(shimPath, AgentSpawnShim.Script.Replace("\r\n", "\n"));

            var socketPath = Path.Combine(dir, AgentIpcPaths.SocketFileName);
            File.Delete(socketPath); // a stale socket from a crashed daemon blocks bind

            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(backlog: 8);

            if (!OperatingSystem.IsWindows())
            {
                // The jail's agent uid must traverse the dir, exec the shim, and connect to the
                // socket (connect needs write on the socket inode). The mount itself is read-only.
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.SetUnixFileMode(shimPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite
                    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            }

            var endpoint = new Endpoint(dir, listener);
            _ = endpoint.AcceptLoopAsync(agentId, handler);
            return endpoint;
        }

        private async Task AcceptLoopAsync(string agentId, AgentIpcHandler handler)
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                Socket connection;
                try
                {
                    connection = await _listener.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    return; // listener closed (teardown) — the loop's normal end
                }

                _ = ServeConnectionAsync(connection, agentId, handler, ct);
            }
        }

        private static async Task ServeConnectionAsync(
            Socket connection, string agentId, AgentIpcHandler handler, CancellationToken ct)
        {
            using (connection)
            await using (var stream = new NetworkStream(connection, ownsSocket: false))
            {
                AgentIpcResponse response;
                try
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    var request = line is null ? null : AgentIpcProtocol.TryParseRequest(line);
                    response = request is null
                        ? new AgentIpcResponse(Ok: false, Error: "malformed request (expected one JSON line)")
                        : await handler(request, agentId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    response = new AgentIpcResponse(Ok: false, Error: ex.Message);
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(AgentIpcProtocol.SerializeResponse(response) + "\n");
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Client hung up mid-response — nothing to salvage.
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            try
            {
                _listener.Dispose();
            }
            catch
            {
                // Already closed.
            }

            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; the dir may be mount-busy until the jail is removed.
            }

            _cts.Dispose();
        }
    }
}
