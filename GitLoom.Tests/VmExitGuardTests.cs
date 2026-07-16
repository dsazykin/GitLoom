using GitLoom.App.Services;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// PR3 — the full-exit warning decision: confirm exactly when the exit would stop the VM
/// (<c>StopVmOnExit</c>) while agents are live. Every other combination exits silently.
/// </summary>
public class VmExitGuardTests
{
    [Theory]
    [InlineData(true, 1, true)]   // VM stops + a live agent → warn
    [InlineData(true, 4, true)]
    [InlineData(true, 0, false)]  // VM stops, nothing running → silent
    [InlineData(false, 4, false)] // VM kept → agents survive the exit → silent
    [InlineData(false, 0, false)]
    public void ShouldConfirm_OnlyWhenVmStopWouldKillLiveAgents(bool stopVm, int liveAgents, bool expected)
    {
        Assert.Equal(expected, VmExitGuard.ShouldConfirm(stopVm, liveAgents));
    }

    [Fact]
    public void Message_NamesTheCount_AndTheConsequence()
    {
        Assert.StartsWith("1 agent is", VmExitGuard.Message(1));
        Assert.StartsWith("3 agents are", VmExitGuard.Message(3));
        Assert.Contains("stops the GitLoom environment", VmExitGuard.Message(3));
        Assert.Contains("branches", VmExitGuard.Message(3)); // the honest "work is kept" fact
    }

    [Fact]
    public void ConfirmButton_NamesTheConsequence_NotOk()
    {
        Assert.Equal("Exit and stop agents", VmExitGuard.ConfirmButtonText);
    }
}
