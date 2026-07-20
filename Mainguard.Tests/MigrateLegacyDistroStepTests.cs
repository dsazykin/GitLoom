using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Phase-4 re-register migration (<see cref="MigrateLegacyDistroStep"/>): a pre-rebrand
/// <c>GitLoomEnv</c> is re-registered as <c>MainguardEnv</c> (export → import → unregister-legacy) on an
/// upgrade, while a fresh install is a no-op. All scoped to the two NAMED distros; the step is
/// best-effort and NEVER hard-stops setup — a failed export leaves the legacy distro in place so a fresh
/// provision can follow. Drives the pure <see cref="IWslRunner"/> seam (no real WSL).
/// </summary>
public sealed class MigrateLegacyDistroStepTests
{
    private static BootstrapOptions Options() =>
        new(InstallDir: @"C:\mg\vm", TarballPath: @"C:\mg\payload\MainguardOS.tar.gz");

    [Fact]
    public async Task FreshInstall_NoLegacyDistro_IsSatisfied_AndDoesNotMigrate()
    {
        var runner = new RecordingRunner(_ => Ok("Ubuntu\ndocker-desktop\n"));
        var step = new MigrateLegacyDistroStep(runner, Options());

        Assert.True(await step.IsSatisfiedAsync(CancellationToken.None));
        Assert.DoesNotContain(runner.Calls, c => Verb(c) is "--export" or "--import");
    }

    [Fact]
    public async Task NewDistroAlreadyPresent_IsSatisfied()
    {
        var runner = new RecordingRunner(_ => Ok($"Ubuntu\n{WslCommands.DistroName}\n"));
        var step = new MigrateLegacyDistroStep(runner, Options());

        Assert.True(await step.IsSatisfiedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Upgrade_LegacyPresent_ReRegisters_ExportThenImportThenUnregisterLegacy()
    {
        var runner = new RecordingRunner(args => Verb(args) switch
        {
            "--list" => Ok($"Ubuntu\n{WslCommands.LegacyDistroName}\n"),
            _ => Ok(""), // export / import / unregister all succeed
        });
        var step = new MigrateLegacyDistroStep(runner, Options());

        Assert.False(await step.IsSatisfiedAsync(CancellationToken.None));

        await step.ExecuteAsync(new Progress<string>(), CancellationToken.None);

        var export = runner.Calls.FindIndex(c => Verb(c) == "--export");
        var import = runner.Calls.FindIndex(c => Verb(c) == "--import");
        var unregLegacy = runner.Calls.FindIndex(
            c => Verb(c) == "--unregister" && c.Contains(WslCommands.LegacyDistroName));

        Assert.True(export >= 0 && import >= 0 && unregLegacy >= 0, "all three re-register verbs must run");
        Assert.True(export < import && import < unregLegacy, "order must be export → import → unregister-legacy");
        Assert.Contains(WslCommands.LegacyDistroName, runner.Calls[export]);
        Assert.Contains(WslCommands.DistroName, runner.Calls[import]);

        // One-shot: after an attempt the step reports satisfied so the chain never hard-stops on it.
        Assert.True(await step.IsSatisfiedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Upgrade_ExportFails_LeavesLegacyInPlace_AndNeverImports()
    {
        var runner = new RecordingRunner(args => Verb(args) switch
        {
            "--list" => Ok($"{WslCommands.LegacyDistroName}\n"),
            "--export" => Fail("not enough disk space"),
            _ => Ok(""),
        });
        var step = new MigrateLegacyDistroStep(runner, Options());

        await step.ExecuteAsync(new Progress<string>(), CancellationToken.None);

        // A failed export must not import or unregister the legacy distro — a fresh provision follows.
        Assert.DoesNotContain(runner.Calls, c => Verb(c) == "--import");
        Assert.DoesNotContain(runner.Calls, c => Verb(c) == "--unregister" && c.Contains(WslCommands.LegacyDistroName));
        // Best-effort: still satisfied (one-shot) so setup proceeds rather than throwing.
        Assert.True(await step.IsSatisfiedAsync(CancellationToken.None));
    }

    private static string Verb(IReadOnlyList<string> args) => args.Count > 0 ? args[0] : string.Empty;
    private static WslRunResult Ok(string stdout) => new(0, stdout, "");
    private static WslRunResult Fail(string stderr) => new(1, "", stderr);

    private sealed class RecordingRunner : IWslRunner
    {
        private readonly Func<IReadOnlyList<string>, WslRunResult> _respond;
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public RecordingRunner(Func<IReadOnlyList<string>, WslRunResult> respond) => _respond = respond;

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(_respond(args));
        }
    }
}
