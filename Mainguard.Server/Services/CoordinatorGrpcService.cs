using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using CoreConv = Mainguard.Agents.Agents.Orchestrator;

namespace Mainguard.Server.Services;

/// <summary>
/// gRPC transport for <see cref="CoordinatorService"/> (P2-14 / P2-47 #9). Validation + dispatch only —
/// the transcript, the reply loop, and the bridge to <see cref="CoreConv.CoordinatorAgent"/> all live in
/// the daemon-side <see cref="CoreConv.CoordinatorConversationService"/>. There is no merge/git/worktree
/// capability on this surface (the coordinator cannot escalate through chat).
/// </summary>
public sealed class CoordinatorGrpcService : CoordinatorService.CoordinatorServiceBase
{
    private readonly CoreConv.CoordinatorConversationService _conversation;
    private readonly ILogger _log;

    public CoordinatorGrpcService(CoreConv.CoordinatorConversationService conversation, ILoggerFactory loggerFactory)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Coordinator);
    }

    public override async Task StreamConversation(
        StreamConversationRequest request,
        IServerStreamWriter<ConversationUpdate> responseStream,
        ServerCallContext context)
    {
        _log.LogInformation("StreamConversation attached peer={Peer}", context.Peer);
        using var signal = new SemaphoreSlim(0);
        void OnChanged() => signal.Release();
        _conversation.Changed += OnChanged;
        try
        {
            await responseStream.WriteAsync(Snapshot()).ConfigureAwait(false);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(Snapshot()).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal teardown.
        }
        finally
        {
            _conversation.Changed -= OnChanged;
        }
    }

    public override async Task<SendMessageResponse> SendMessage(SendMessageRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "text is required."));
        }

        await _conversation.SendAsync(request.Text, context.CancellationToken).ConfigureAwait(false);
        _log.LogInformation("SendMessage accepted ({Length} chars)", request.Text.Length);
        return new SendMessageResponse { Accepted = true };
    }

    private ConversationUpdate Snapshot()
    {
        var update = new ConversationUpdate();
        foreach (var turn in _conversation.Snapshot())
        {
            update.Turns.Add(new ConversationTurn
            {
                Seq = turn.Seq,
                Role = turn.Role.ToString(),
                Text = turn.Text,
                PlanId = turn.PlanId ?? string.Empty,
            });
        }

        return update;
    }
}
