using System.Collections.Generic;
using GitLoom.App.Services;
using GitLoom.App.ViewModels.Agents;
using Xunit;

namespace GitLoom.Tests;

// P2-13 test 6 (§5) / TI-P2-13.3: an OS notification fires on a transition into waiting/blocked,
// EXCEPT when the app is foregrounded on that very agent. A fake notifier records the calls.
public class NotificationSuppressionTests
{
    private sealed class RecordingNotifier : IAgentNotifier
    {
        public List<(string Title, string Body)> Calls { get; } = new();
        public void Notify(string title, string body) => Calls.Add((title, body));
    }

    [Fact]
    public void Notifications_SuppressedWhenForegrounded()
    {
        var notifier = new RecordingNotifier();
        var foregroundedOn = "loom-1";
        var service = new AgentNotificationService(
            notifier,
            isAppForegrounded: () => true,
            foregroundedAgentId: () => foregroundedOn);

        // Baseline (first observation never notifies), then transition into AwaitingReview while
        // the app is foregrounded on that agent → suppressed.
        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.Working);
        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.AwaitingReview);
        Assert.Empty(notifier.Calls);
    }

    [Fact]
    public void Notifications_FireWhenNotForegroundedOnThatAgent()
    {
        var notifier = new RecordingNotifier();
        var service = new AgentNotificationService(
            notifier,
            isAppForegrounded: () => true,
            foregroundedAgentId: () => "some-other-agent");

        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.Working);
        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.AwaitingReview);

        // Foregrounded, but on a DIFFERENT agent → still notified.
        Assert.Single(notifier.Calls);
        Assert.Contains("Loom-1", notifier.Calls[0].Title);
    }

    [Fact]
    public void Notifications_FireWhenAppBackgrounded()
    {
        var notifier = new RecordingNotifier();
        var service = new AgentNotificationService(
            notifier,
            isAppForegrounded: () => false,
            foregroundedAgentId: () => "loom-1");

        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.Working);
        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.Conflict);
        Assert.Single(notifier.Calls);
    }

    [Fact]
    public void Notifications_DoNotFireOnLateralWaitingTransition()
    {
        var notifier = new RecordingNotifier();
        var service = new AgentNotificationService(
            notifier,
            isAppForegrounded: () => false,
            foregroundedAgentId: () => null);

        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.AwaitingReview); // baseline (already waiting)
        service.OnStatusChanged("loom-1", "Loom-1", AgentStatus.Conflict);       // lateral within the set
        Assert.Empty(notifier.Calls);
    }
}
