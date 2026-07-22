using System;
using System.Threading.Tasks;
using Mainguard.Agents.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The egress block-notification prompt VM (Fix 2): Unblock invokes the add-and-retry callback with the
/// blocked host; a failure surfaces its message and keeps the prompt open; Keep blocked dismisses; the
/// message names what was blocked and which agent needed it.
/// </summary>
public sealed class EgressBlockPromptViewModelTests
{
    [Fact]
    public async Task Unblock_InvokesTheCallback_WithTheBlockedHost()
    {
        string? unblocked = null;
        var vm = new EgressBlockPromptViewModel("platform.claude.com", "claude-code",
            host => { unblocked = host; return Task.CompletedTask; }, dismiss: () => { });

        await vm.UnblockCommand.ExecuteAsync(null);

        Assert.Equal("platform.claude.com", unblocked);
        Assert.Equal("", vm.Error);
    }

    [Fact]
    public async Task Unblock_Failure_SurfacesTheMessage_AndStaysOpen()
    {
        var vm = new EgressBlockPromptViewModel("platform.claude.com", "claude-code",
            _ => throw new InvalidOperationException("daemon refused"), dismiss: () => { });

        await vm.UnblockCommand.ExecuteAsync(null);

        Assert.Contains("daemon refused", vm.Error);
        Assert.False(vm.IsBusy); // re-enabled so the user can retry or keep it blocked
    }

    [Fact]
    public void KeepBlocked_Dismisses()
    {
        var dismissed = false;
        var vm = new EgressBlockPromptViewModel("h.example.com", "codex",
            _ => Task.CompletedTask, dismiss: () => dismissed = true);

        vm.KeepBlockedCommand.Execute(null);

        Assert.True(dismissed);
    }

    [Fact]
    public void Message_NamesTheHostAndAgent()
    {
        var vm = new EgressBlockPromptViewModel("platform.claude.com", "claude-code",
            _ => Task.CompletedTask, () => { });

        Assert.Contains("claude-code", vm.Message);
        Assert.Contains("platform.claude.com", vm.Message);
    }
}
