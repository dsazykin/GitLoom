using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Orchestrator;

/// <summary>The kind of a conversation turn (mirrors the UI's ChatLineKind, transport-agnostic).</summary>
public enum ConversationRole { Human, Coordinator, ToolCall, SystemLine, PlanCard }

/// <summary>One turn in the coordinator conversation, in monotonic <see cref="Seq"/> order.</summary>
public sealed record ConversationTurn(long Seq, ConversationRole Role, string Text, string? PlanId = null);

/// <summary>
/// The daemon-side reply engine the conversation drives. The real production engine
/// (<see cref="CoordinatorAgentReplyEngine"/>) forwards to a <see cref="CoordinatorAgent"/> tool loop; a
/// deterministic engine backs the integration test. Kept as a seam so the LLM adapter (the one leg that
/// needs a live model) stays pluggable — the conversation store + streaming are real regardless.
/// </summary>
public interface ICoordinatorReplyEngine
{
    Task<string> ReplyAsync(string humanMessage, CancellationToken ct);
}

/// <summary>The production reply engine: a thin bridge onto the existing <see cref="CoordinatorAgent"/>
/// (P2-14). It adds no orchestration logic — it forwards the human message into the agent's capped
/// tool loop and returns the coordinator's textual reply.</summary>
public sealed class CoordinatorAgentReplyEngine : ICoordinatorReplyEngine
{
    private readonly CoordinatorAgent _agent;

    public CoordinatorAgentReplyEngine(CoordinatorAgent agent)
        => _agent = agent ?? throw new ArgumentNullException(nameof(agent));

    public Task<string> ReplyAsync(string humanMessage, CancellationToken ct)
        => _agent.SendAsync(humanMessage, ct);
}

/// <summary>
/// P2-14 / P2-47 #9 — the coordinator conversation the daemon serves over
/// <c>CoordinatorService</c>. It owns the real transcript (seq-ordered turns) and a <see cref="Changed"/>
/// event the gRPC stream re-pushes on. <see cref="SendAsync"/> appends the human turn, drives the
/// (optional) <see cref="ICoordinatorReplyEngine"/> — the real production engine bridges to
/// <see cref="CoordinatorAgent"/> — and appends the coordinator's reply turn. When no engine is
/// configured (no coordinator model wired), it records an honest system turn rather than fabricating a
/// reply: the store + streaming stay live, but nothing is invented.
/// </summary>
public sealed class CoordinatorConversationService
{
    private readonly object _gate = new();
    private readonly List<ConversationTurn> _turns = new();
    private readonly ICoordinatorReplyEngine? _engine;
    private long _seq;

    public CoordinatorConversationService(ICoordinatorReplyEngine? engine = null)
    {
        _engine = engine;
    }

    /// <summary>Raised after any turn is appended (the stream re-pushes the snapshot).</summary>
    public event Action? Changed;

    /// <summary>True when a coordinator reply engine is wired (a live model is available).</summary>
    public bool HasEngine => _engine is not null;

    /// <summary>The conversation so far, in seq order.</summary>
    public IReadOnlyList<ConversationTurn> Snapshot()
    {
        lock (_gate)
        {
            return _turns.ToArray();
        }
    }

    /// <summary>
    /// Handle one operator message: append it, drive the coordinator reply engine, append the reply.
    /// Both the human turn (immediately) and the reply turn raise <see cref="Changed"/> so a live stream
    /// shows the message the instant it is accepted, then the reply when it lands.
    /// </summary>
    public async Task SendAsync(string text, CancellationToken ct = default)
    {
        Append(ConversationRole.Human, text ?? string.Empty);

        if (_engine is null)
        {
            Append(ConversationRole.SystemLine,
                "No coordinator model is configured on this daemon — the message was recorded but not answered.");
            return;
        }

        string reply;
        try
        {
            reply = await _engine.ReplyAsync(text ?? string.Empty, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Append(ConversationRole.SystemLine, $"The coordinator could not reply: {ex.Message}");
            return;
        }

        Append(ConversationRole.Coordinator, reply);
    }

    /// <summary>Append a turn from outside the reply loop (e.g. a system/plan line the daemon injects).</summary>
    public void Append(ConversationRole role, string text, string? planId = null)
    {
        ConversationTurn turn;
        lock (_gate)
        {
            turn = new ConversationTurn(++_seq, role, text ?? string.Empty, planId);
            _turns.Add(turn);
        }

        Changed?.Invoke();
    }
}
