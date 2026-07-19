namespace Mainguard.App.Shell.Services;

/// <summary>
/// The pure decision behind the full-exit confirmation: a FULL exit terminates the GitLoomEnv VM
/// when <c>UserPreferences.StopVmOnExit</c> is on, which kills every live agent CLI mid-flight —
/// so that exact combination (and only it) warns first. Hiding to the tray never stops the VM and
/// never warns; with the setting off, the VM (and the agents) outlive the app.
/// </summary>
public static class VmExitGuard
{
    /// <summary>True when a full exit must be confirmed: it would stop the VM under live agents.</summary>
    public static bool ShouldConfirm(bool stopVmOnExit, int liveAgentCount)
        => stopVmOnExit && liveAgentCount > 0;

    /// <summary>The confirmation dialog's title.</summary>
    public const string Title = "Exit Mainguard?";

    /// <summary>The confirm button's verb — names the consequence, not "OK".</summary>
    public const string ConfirmButtonText = "Exit and stop agents";

    /// <summary>The consequence, stated plainly (V-4: what will change).</summary>
    public static string Message(int liveAgentCount) => liveAgentCount == 1
        ? "1 agent is still running. Exiting stops the Mainguard environment and terminates it mid-task. "
          + "Its work stays on its branch, but the session cannot be resumed."
        : $"{liveAgentCount} agents are still running. Exiting stops the Mainguard environment and terminates them mid-task. "
          + "Their work stays on their branches, but the sessions cannot be resumed.";
}
