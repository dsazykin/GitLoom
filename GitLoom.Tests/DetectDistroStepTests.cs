using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Audit fix for the zero-distro fresh machine: <c>wsl --list --quiet</c> exits NON-ZERO when WSL is
/// enabled but no distribution is installed yet — exactly the state <c>wsl --install
/// --no-distribution</c> (the diagnostics-mandated enablement) leaves behind. That must count as
/// "WSL2 present" (the very next step imports the first distro), while a genuinely broken WSL still
/// fails actionably.
/// </summary>
public sealed class DetectDistroStepTests
{
    // Captured shapes of the real zero-distro failure across WSL builds (store + inbox).
    public static IEnumerable<object[]> NoDistroOutputs() => new[]
    {
        new object[] { new WslRunResult(-1, "Windows Subsystem for Linux has no installed distributions.\r\nUse 'wsl.exe --list --online' to list available distributions.\r\n", "") },
        new object[] { new WslRunResult(1, "", "Windows Subsystem for Linux has no installed distributions.\r\nDistributions can be installed by visiting the Microsoft Store:\r\nhttps://aka.ms/wslstore\r\n") },
        new object[] { new WslRunResult(1, "", "There is no distribution with the supplied name.\r\nError code: Wsl/WSL_E_DEFAULT_DISTRO_NOT_FOUND\r\n") },
    };

    [Theory]
    [MemberData(nameof(NoDistroOutputs))]
    public async Task ZeroDistroMachine_IsSatisfied_NeverThrowsNotInstalled(WslRunResult listResult)
    {
        var step = new DetectDistroStep(new ScriptedRunner(listResult));

        Assert.True(await step.IsSatisfiedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HealthyWslWithDistros_IsSatisfied()
    {
        var step = new DetectDistroStep(new ScriptedRunner(new WslRunResult(0, "Ubuntu\nGitLoomEnv\n", "")));

        Assert.True(await step.IsSatisfiedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task BrokenWsl_ComponentNotEnabled_StillFailsActionably()
    {
        // The component-missing error also suggests `wsl --install` — that generic hint must NOT be
        // read as "zero distros"; this state is a real failure the enablement flow owns.
        var result = new WslRunResult(-1, "",
            "The Windows Subsystem for Linux optional component is not enabled. "
            + "Please enable it and try again. Run 'wsl --install' or see https://aka.ms/wslinstall\r\n");
        var step = new DetectDistroStep(new ScriptedRunner(result));

        Assert.False(await step.IsSatisfiedAsync(CancellationToken.None));
        await Assert.ThrowsAsync<WslNotInstalledException>(
            () => step.ExecuteAsync(new Progress<string>(), CancellationToken.None));
    }

    private sealed class ScriptedRunner : IWslRunner
    {
        private readonly WslRunResult _result;
        public ScriptedRunner(WslRunResult result) => _result = result;

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
            => Task.FromResult(_result);
    }
}
