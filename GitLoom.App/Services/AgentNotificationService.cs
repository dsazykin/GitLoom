using System;
using System.Collections.Generic;
using Avalonia.Controls.Notifications;
using GitLoom.App.ViewModels.Agents;

namespace GitLoom.App.Services;

/// <summary>The toast sink. Abstracted so the OS/in-window path is swapped for a fake in tests.</summary>
public interface IAgentNotifier
{
    void Notify(string title, string body);
}

/// <summary>
/// Fires an OS/in-window notification when an agent transitions INTO a waiting/blocked state
/// (AwaitingReview or Conflict), and suppresses it when the app is already foregrounded on that
/// very agent (P2-13 §6). Transition semantics: the first time an agent is seen its status is
/// recorded as a baseline without a toast, so startup never spams — only a genuine change fires.
/// </summary>
public sealed class AgentNotificationService
{
    private readonly IAgentNotifier _notifier;
    private readonly Func<bool> _isAppForegrounded;
    private readonly Func<string?> _foregroundedAgentId;
    private readonly Dictionary<string, AgentStatus> _last = new();

    /// <param name="notifier">Where toasts go.</param>
    /// <param name="isAppForegrounded">True when the main window is active.</param>
    /// <param name="foregroundedAgentId">The agent whose workspace is currently in focus, or null.</param>
    public AgentNotificationService(IAgentNotifier notifier, Func<bool> isAppForegrounded, Func<string?> foregroundedAgentId)
    {
        _notifier = notifier;
        _isAppForegrounded = isAppForegrounded;
        _foregroundedAgentId = foregroundedAgentId;
    }

    /// <summary>Observe an agent's current status; may raise a toast on a waiting/blocked transition.</summary>
    public void OnStatusChanged(string agentId, string name, AgentStatus status)
    {
        var seen = _last.TryGetValue(agentId, out var previous);
        _last[agentId] = status;
        if (!seen) return; // baseline the first observation; a toast needs a real transition

        if (!AttentionPolicy.IsWaitingOrBlockedTransition(previous, status)) return;

        // Suppressed only when foregrounded ON THAT agent — foregrounded on a different agent still notifies.
        if (_isAppForegrounded() && string.Equals(_foregroundedAgentId(), agentId, StringComparison.Ordinal))
            return;

        _notifier.Notify($"{name} needs you", BodyFor(status));
    }

    /// <summary>Drop an agent's tracked state on teardown so a re-spawn re-baselines cleanly.</summary>
    public void Forget(string agentId) => _last.Remove(agentId);

    private static string BodyFor(AgentStatus status) => status switch
    {
        AgentStatus.AwaitingReview => "Ready for your review.",
        AgentStatus.Conflict => "Blocked on a conflict — needs your input.",
        _ => "Waiting on you.",
    };
}

/// <summary>
/// The default notifier: Avalonia's in-window <see cref="WindowNotificationManager"/> (the
/// cross-platform fallback). A true OS-native toast on Windows is a follow-up
/// (<c>// TODO(P2-13): native WinRT toast when a TopLevel is unavailable</c>).
/// </summary>
public sealed class WindowNotificationAgentNotifier : IAgentNotifier
{
    private readonly WindowNotificationManager _manager;

    public WindowNotificationAgentNotifier(WindowNotificationManager manager) => _manager = manager;

    public void Notify(string title, string body) =>
        _manager.Show(new Notification(title, body, NotificationType.Information));
}
