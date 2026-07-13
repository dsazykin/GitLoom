using System.Threading.Tasks;
using GitLoom.Protos.V1;
using GitLoom.Server.Runtime;
using Grpc.Core;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="AgentService"/>. Validation + dispatch ONLY — all
/// state lives in <see cref="AgentSessionStore"/> so the behavior is unit-testable
/// without the transport (P2-02 rejection trigger: no business logic in gRPC classes).
/// </summary>
public sealed class AgentGrpcService : AgentService.AgentServiceBase
{
    private readonly AgentSessionStore _store;

    public AgentGrpcService(AgentSessionStore store)
    {
        _store = store;
    }

    public override Task<SpawnAgentResponse> SpawnAgent(SpawnAgentRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.AgentKind))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent_kind is required."));
        }

        var session = _store.Spawn(request.AgentKind);
        return Task.FromResult(new SpawnAgentResponse { AgentId = session.Id });
    }

    public override Task<StopAgentResponse> StopAgent(StopAgentRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "agent_id is required."));
        }

        return Task.FromResult(new StopAgentResponse { Stopped = _store.Stop(request.AgentId) });
    }

    public override Task<ListAgentsResponse> ListAgents(ListAgentsRequest request, ServerCallContext context)
    {
        var response = new ListAgentsResponse();
        foreach (var session in _store.List())
        {
            response.Agents.Add(new AgentInfo
            {
                AgentId = session.Id,
                AgentKind = session.Kind,
                State = session.State,
            });
        }

        return Task.FromResult(response);
    }

    public override async Task StreamAgentEvents(
        StreamAgentEventsRequest request,
        IServerStreamWriter<AgentEvent> responseStream,
        ServerCallContext context)
    {
        var reader = _store.Subscribe(out var unsubscribe);
        try
        {
            await foreach (var delta in reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(Map(delta));
            }
        }
        catch (System.OperationCanceledException)
        {
            // Client detached — normal stream teardown.
        }
        finally
        {
            unsubscribe();
        }
    }

    private static AgentEvent Map(AgentDelta delta)
    {
        var evt = new AgentEvent { AgentId = delta.AgentId, Seq = delta.Seq };
        switch (delta.Kind)
        {
            case "snapshot":
                var snapshot = new AgentSnapshot();
                foreach (var entry in delta.Payload.Split(',', System.StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = entry.Split(':');
                    snapshot.Agents.Add(new AgentInfo
                    {
                        AgentId = parts[0],
                        AgentKind = parts.Length > 1 ? parts[1] : string.Empty,
                        State = parts.Length > 2 ? parts[2] : string.Empty,
                    });
                }

                evt.Snapshot = snapshot;
                break;
            case "log":
                evt.Log = new LogLine { Line = delta.Payload };
                break;
            default:
                evt.State = new StateChange { State = delta.Payload };
                break;
        }

        return evt;
    }
}
