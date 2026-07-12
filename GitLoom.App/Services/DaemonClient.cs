using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Daemon;
using GitLoom.Protos.V1;
using Grpc.Core;
using Grpc.Net.Client;

namespace GitLoom.App.Services;

/// <summary>
/// The App's sole daemon touch-point (G-18): a gRPC client over loopback. Owns channel
/// creation, bearer-token metadata (read from the daemon's session-token file),
/// reconnect-with-exponential-backoff+jitter (cap ~30 s), and a
/// <see cref="ConnectionState"/> observable property the P2-13 Activity Bar binds to
/// (plain <see cref="INotifyPropertyChanged"/> — no Rx). <see cref="StreamAgentEventsAsync"/>
/// resumes via the server's snapshot-then-deltas design after any drop.
///
/// Every RPC method takes a <see cref="CancellationToken"/> and applies a deadline —
/// there is no deadline-less call path (P2-02 rejection trigger).
/// </summary>
public sealed class DaemonClient : INotifyPropertyChanged, IDisposable
{
    private static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(10);

    private readonly Func<GrpcChannel> _channelFactory;
    private readonly Func<string> _tokenProvider;
    private readonly BackoffPolicy _backoff;
    private GrpcChannel? _channel;
    private ConnectionState _state = ConnectionState.Down;

    public DaemonClient(Func<GrpcChannel> channelFactory, Func<string> tokenProvider, BackoffPolicy? backoff = null)
    {
        _channelFactory = channelFactory;
        _tokenProvider = tokenProvider;
        _backoff = backoff ?? BackoffPolicy.Default;
    }

    /// <summary>Production factory: loopback channel + token read from the daemon token file.</summary>
    public static DaemonClient ForLoopback(int port = DaemonPaths.DefaultLoopbackPort, string? tokenPath = null)
    {
        var path = tokenPath ?? DaemonPaths.TokenFilePath();
        // h2c: the daemon serves gRPC over cleartext HTTP/2 on loopback.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        return new DaemonClient(
            () => GrpcChannel.ForAddress($"http://127.0.0.1:{port}"),
            () => File.ReadAllText(path).Trim());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Fires on every connection-state transition (non-XAML consumers).</summary>
    public event Action<ConnectionState>? ConnectionStateChanged;

    /// <summary>Raised for each agent event received on the live stream.</summary>
    public event Action<AgentEvent>? AgentEventReceived;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            ConnectionStateChanged?.Invoke(value);
        }
    }

    /// <summary>Lists agents (authenticated, deadlined).</summary>
    public async Task<IReadOnlyList<AgentInfo>> ListAgentsAsync(CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.ListAgentsAsync(new ListAgentsRequest(), CallOptions(ct, deadline));
        return response.Agents;
    }

    /// <summary>Spawns an agent (authenticated, deadlined). The model key is a `// SECRET` field.</summary>
    public async Task<string> SpawnAgentAsync(
        string repoHandle, string taskPrompt, string agentKind, string modelApiKey,
        CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = repoHandle,
            TaskPrompt = taskPrompt,
            AgentKind = agentKind,
            ModelApiKey = modelApiKey,
        }, CallOptions(ct, deadline));
        return response.AgentId;
    }

    /// <summary>Stops an agent (authenticated, deadlined).</summary>
    public async Task<bool> StopAgentAsync(string agentId, CancellationToken ct, TimeSpan? deadline = null)
    {
        var client = new AgentService.AgentServiceClient(Channel());
        var response = await client.StopAgentAsync(new StopAgentRequest { AgentId = agentId }, CallOptions(ct, deadline));
        return response.Stopped;
    }

    /// <summary>
    /// Runs the agent-event stream with reconnect. Yields every <see cref="AgentEvent"/>
    /// (also raised on <see cref="AgentEventReceived"/>). On a transient fault it marks
    /// <see cref="ConnectionState.Degraded"/>, backs off (capped, jittered), rebuilds the
    /// channel, and re-subscribes — the fresh server snapshot re-syncs the client. A
    /// missing/wrong token is terminal (no retry storm): the state goes
    /// <see cref="ConnectionState.Down"/> and the loop exits until re-invoked (a re-read
    /// of the token file). Ends when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> StreamAgentEventsAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var faulted = false;
            var permissionDenied = false;
            IAsyncEnumerator<AgentEvent>? enumerator = null;
            try
            {
                var client = new AgentService.AgentServiceClient(Channel());
                var call = client.StreamAgentEvents(new StreamAgentEventsRequest(), AuthOnly(ct));
                enumerator = call.ResponseStream.ReadAllAsync(ct).GetAsyncEnumerator(ct);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                permissionDenied = true;
            }
            catch (RpcException)
            {
                faulted = true;
            }

            if (permissionDenied)
            {
                State = ConnectionState.Down;
                yield break;
            }

            if (enumerator is not null)
            {
                while (true)
                {
                    AgentEvent? current = null;
                    try
                    {
                        if (!await enumerator.MoveNextAsync())
                        {
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        await enumerator.DisposeAsync();
                        State = ConnectionState.Down;
                        yield break;
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
                    {
                        permissionDenied = true;
                        break;
                    }
                    catch (RpcException)
                    {
                        faulted = true;
                        break;
                    }

                    // First successful frame → healthy; reset backoff.
                    State = ConnectionState.Connected;
                    attempt = 0;
                    AgentEventReceived?.Invoke(current);
                    yield return current;
                }

                await enumerator.DisposeAsync();
            }

            if (permissionDenied)
            {
                State = ConnectionState.Down;
                yield break;
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            // Stream ended or faulted → reconnect with backoff.
            _ = faulted;
            State = ConnectionState.Degraded;
            ResetChannel();
            try
            {
                await Task.Delay(_backoff.Delay(attempt++), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        State = ConnectionState.Down;
    }

    /// <summary>
    /// Opens the terminal <c>Attach</c> bidi stream (authenticated, no wall-clock deadline — it is
    /// long-lived, ended by cancelling <paramref name="ct"/>). The caller writes the first
    /// <c>agent_id</c> frame, then input/resize frames, and reads <c>raw</c> output frames.
    /// </summary>
    public AsyncDuplexStreamingCall<TerminalInput, TerminalOutput> AttachTerminal(CancellationToken ct)
    {
        var client = new TerminalService.TerminalServiceClient(Channel());
        return client.Attach(AuthOnly(ct));
    }

    private GrpcChannel Channel() => _channel ??= _channelFactory();

    private void ResetChannel()
    {
        var old = _channel;
        _channel = null;
        old?.Dispose();
    }

    private Metadata AuthHeaders() => new() { { "authorization", $"bearer {_tokenProvider()}" } };

    private CallOptions CallOptions(CancellationToken ct, TimeSpan? deadline)
        => new(headers: AuthHeaders(), deadline: DateTime.UtcNow.Add(deadline ?? DefaultDeadline), cancellationToken: ct);

    // Streaming calls carry no wall-clock deadline (they are long-lived) but always a
    // cancellation token — the caller ends them by cancelling.
    private CallOptions AuthOnly(CancellationToken ct) => new(headers: AuthHeaders(), cancellationToken: ct);

    public void Dispose() => ResetChannel();
}

/// <summary>
/// Exponential backoff with full jitter, capped. Extracted so it is unit-testable
/// without a network (the client-side thin test asserts the cap).
/// </summary>
public sealed class BackoffPolicy
{
    public static readonly BackoffPolicy Default = new(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30));

    private readonly TimeSpan _base;
    private readonly TimeSpan _cap;
    private readonly Random _random;

    public BackoffPolicy(TimeSpan @base, TimeSpan cap, Random? random = null)
    {
        _base = @base;
        _cap = cap;
        _random = random ?? Random.Shared;
    }

    public TimeSpan Cap => _cap;

    /// <summary>The (jittered) delay for a zero-based attempt, never exceeding the cap.</summary>
    public TimeSpan Delay(int attempt)
    {
        // base * 2^attempt, clamped to cap, then full jitter in [0, ceiling].
        var exponent = Math.Min(attempt, 30);
        var ceilingMs = Math.Min(_cap.TotalMilliseconds, _base.TotalMilliseconds * Math.Pow(2, exponent));
        var jittered = _random.NextDouble() * ceilingMs;
        return TimeSpan.FromMilliseconds(jittered);
    }
}
