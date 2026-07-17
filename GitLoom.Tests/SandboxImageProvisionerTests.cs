using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// v1 sandbox-image provisioning (field failure 2026-07-17, twice: the CI-built jail images never
/// reach installed VMs — a fresh GitLoomEnv import and the tier-2 upgrade both leave an empty
/// docker store, so the first spawn fails). Covers the in-distro probe parsing, the exact
/// /mnt-translated build argv (G-12 distro-scoped, never a VM-wide verb), the serialized build
/// order, per-image failure isolation, the missing-bundled-sources skip, and the
/// <see cref="SandboxImageAutoProvision"/> orchestration + toast policy — all over a fake
/// <see cref="IWslRunner"/>, never real docker.
/// </summary>
public class SandboxImageProvisionerTests : IDisposable
{
    private readonly string _tempRoot;

    public SandboxImageProvisionerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "gl-imgprov-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (Exception)
        {
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Creates <c>&lt;root&gt;/&lt;name&gt;/Dockerfile</c> for the given specs and returns the root.</summary>
    private string BundledSources(params SandboxImageSpec[] specs)
    {
        foreach (var spec in specs)
        {
            var dir = Path.Combine(_tempRoot, spec.SourceDirName);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "Dockerfile"), "FROM scratch\n");
        }

        Directory.CreateDirectory(_tempRoot);
        return _tempRoot;
    }

    // ---- /mnt path translation ----------------------------------------------------------------

    [Fact]
    public void ToVmPath_TranslatesTheWindowsSourceDir_ToItsDrvfsForm()
    {
        Assert.Equal(
            "/mnt/c/Program Files/GitLoom/payload/images/gitloom-agent-base",
            SandboxImageProvisioner.ToVmPath(@"C:\Program Files\GitLoom\payload\images\gitloom-agent-base"));
    }

    [Fact]
    public void ToVmPath_PassesNativeLinuxPathsThrough()
    {
        Assert.Equal("/tmp/payload/images", SandboxImageProvisioner.ToVmPath("/tmp/payload/images"));
    }

    // ---- Command shapes (G-12: distro-scoped, never a VM-wide verb) ---------------------------

    [Fact]
    public void BuildImage_EmitsTheExactManualFieldUnblockArgv()
    {
        Assert.Equal(
            new[]
            {
                "-d", "GitLoomEnv", "--", "docker", "build", "-t", "gitloom-agent-base:latest",
                "/mnt/c/Program Files/GitLoom/payload/images/gitloom-agent-base",
            },
            SandboxImageCommands.BuildImage(
                "gitloom-agent-base:latest",
                "/mnt/c/Program Files/GitLoom/payload/images/gitloom-agent-base"));
    }

    [Fact]
    public void AllBuilders_AreDistroScoped_AndNeverEmitTheVmWideShutdownVerb()
    {
        foreach (var builder in SandboxImageCommands.AllBuilders())
        {
            Assert.Equal(new[] { "-d", "GitLoomEnv", "--" }, builder.Take(3));
            Assert.DoesNotContain("--shutdown", builder);
        }
    }

    // ---- Probe --------------------------------------------------------------------------------

    [Fact]
    public async Task Probe_ReportsExactlyTheImagesDockerInspectDoesNotKnow()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains(SandboxImages.EgressProxy.ImageTag)
                ? new WslRunResult(1, "", "Error: No such image: gitloom-egress-proxy:latest")
                : new WslRunResult(0, "sha256:abc", ""),
        };

        var missing = await new SandboxImageProvisioner(wsl).ProbeMissingAsync(CancellationToken.None);

        Assert.Equal(new[] { SandboxImages.EgressProxy }, missing);
        Assert.Equal(
            new[]
            {
                new[] { "-d", "GitLoomEnv", "--", "docker", "image", "inspect", "--format", "{{.Id}}", "gitloom-agent-base:latest" },
                new[] { "-d", "GitLoomEnv", "--", "docker", "image", "inspect", "--format", "{{.Id}}", "gitloom-egress-proxy:latest" },
            },
            wsl.Calls);
    }

    // ---- Provision ----------------------------------------------------------------------------

    [Fact]
    public async Task Provision_BuildsSerialized_InDeclaredOrder_WithTheTranslatedSourcePath()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner();

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            SandboxImages.All, root, _ => { }, progress: null, CancellationToken.None);

        Assert.All(results, r => Assert.Equal(SandboxImageBuildKind.Built, r.Kind));
        Assert.Equal(1, wsl.MaxConcurrency); // never two builds in flight at once
        Assert.Equal(
            new[]
            {
                new[]
                {
                    "-d", "GitLoomEnv", "--", "docker", "build", "-t", "gitloom-agent-base:latest",
                    SandboxImageProvisioner.ToVmPath(Path.Combine(root, "gitloom-agent-base")),
                },
                new[]
                {
                    "-d", "GitLoomEnv", "--", "docker", "build", "-t", "gitloom-egress-proxy:latest",
                    SandboxImageProvisioner.ToVmPath(Path.Combine(root, "gitloom-egress-proxy")),
                },
            },
            wsl.Calls);
    }

    [Fact]
    public async Task Provision_OneImagesFailure_NeverStopsTheNext_AndCarriesTheDockerErrorTail()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("gitloom-agent-base:latest")
                ? new WslRunResult(1, "", "Step 3/9 : RUN apt-get update\nERROR: failed to solve: no route to host")
                : new WslRunResult(0, "", ""),
        };

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            SandboxImages.All, root, _ => { }, progress: null, CancellationToken.None);

        Assert.Equal(SandboxImageBuildKind.BuildFailed, results[0].Kind);
        Assert.Equal("gitloom-agent-base:latest", results[0].ImageTag);
        Assert.Contains("failed to solve: no route to host", results[0].Detail);
        Assert.Equal(SandboxImageBuildKind.Built, results[1].Kind);
        Assert.Equal(2, wsl.Calls.Count); // the second build still ran
    }

    [Fact]
    public async Task Provision_MissingBundledSource_IsATypedSkipNamingThePath_NoBuildIssued()
    {
        Directory.CreateDirectory(_tempRoot); // root exists, but carries no image source dirs
        var wsl = new RecordingWslRunner();

        var results = await new SandboxImageProvisioner(wsl).ProvisionAsync(
            new[] { SandboxImages.AgentBase }, _tempRoot, _ => { }, progress: null, CancellationToken.None);

        Assert.Equal(SandboxImageBuildKind.SkippedMissingSource, results[0].Kind);
        Assert.Contains(Path.Combine(_tempRoot, "gitloom-agent-base"), results[0].Detail);
        Assert.Empty(wsl.Calls);
    }

    // ---- The startup orchestration + toast policy ---------------------------------------------

    [Fact]
    public async Task AutoProvision_AllPresent_IsSilent_NothingBuilt_NoToast()
    {
        var wsl = new RecordingWslRunner(); // every inspect answers 0 → nothing missing
        SandboxImageProvisionOutcome? outcome = null;
        var log = new List<string>();

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), BundledSources(), log.Add, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.AllPresent, outcome!.Kind);
        Assert.Null(SandboxImageToast.TryCompose(outcome));
        Assert.DoesNotContain(wsl.Calls, args => args.Contains("build"));
        Assert.Contains(log, l => l.Contains("nothing to do"));
    }

    [Fact]
    public async Task AutoProvision_MissingWithBundledSources_Installs_AndComposesTheSuccessToast()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner
        {
            // Both inspects miss; both builds succeed.
            Responder = args => args.Contains("inspect") ? new WslRunResult(1, "", "no such image") : new WslRunResult(0, "", ""),
        };
        SandboxImageProvisionOutcome? outcome = null;

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), root, _ => { }, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.Installed, outcome!.Kind);
        var toast = SandboxImageToast.TryCompose(outcome);
        Assert.Equal("Sandbox images installed.", toast!.Message);
        Assert.False(toast.IsWarning);
    }

    [Fact]
    public async Task AutoProvision_BuildFailure_IsAWarningToastPointingAtOobeLog()
    {
        var root = BundledSources(SandboxImages.AgentBase, SandboxImages.EgressProxy);
        var wsl = new RecordingWslRunner
        {
            Responder = args => args.Contains("inspect")
                ? new WslRunResult(1, "", "no such image")
                : new WslRunResult(1, "", "ERROR: failed to solve"),
        };
        SandboxImageProvisionOutcome? outcome = null;

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), root, _ => { }, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.InstallFailed, outcome!.Kind);
        var toast = SandboxImageToast.TryCompose(outcome);
        Assert.True(toast!.IsWarning);
        Assert.Contains("oobe.log", toast.Message);
    }

    [Fact]
    public async Task AutoProvision_MissingWithoutBundledSources_SkipsSilently_NamingThePath()
    {
        var wsl = new RecordingWslRunner
        {
            Responder = _ => new WslRunResult(1, "", "no such image"),
        };
        var missingRoot = Path.Combine(_tempRoot, "payload", "images"); // never created
        SandboxImageProvisionOutcome? outcome = null;
        var log = new List<string>();

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), missingRoot, log.Add, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.SkippedNoBundledSources, outcome!.Kind);
        Assert.Null(SandboxImageToast.TryCompose(outcome));
        Assert.Contains(log, l => l.Contains(missingRoot));
        Assert.DoesNotContain(wsl.Calls, args => args.Contains("build"));
    }

    [Fact]
    public async Task AutoProvision_ProbeFault_NeverThrows_LogsAndStaysToastSilent()
    {
        var wsl = new RecordingWslRunner
        {
            Thrower = _ => new InvalidOperationException("wsl.exe vanished"),
        };
        SandboxImageProvisionOutcome? outcome = null;
        var log = new List<string>();

        await SandboxImageAutoProvision.RunAsync(
            new SandboxImageProvisioner(wsl), BundledSources(), log.Add, CancellationToken.None,
            onOutcome: o => outcome = o);

        Assert.Equal(SandboxImageProvisionOutcomeKind.Faulted, outcome!.Kind);
        Assert.Null(SandboxImageToast.TryCompose(outcome));
        Assert.Contains(log, l => l.Contains("wsl.exe vanished"));
    }

    // ---- fake runner --------------------------------------------------------------------------

    /// <summary>Records every argv; scripted via <see cref="Responder"/> (default: everything
    /// succeeds) or <see cref="Thrower"/>. Tracks concurrent entries to prove serialization.</summary>
    private sealed class RecordingWslRunner : IWslRunner
    {
        private int _inFlight;

        public List<IReadOnlyList<string>> Calls { get; } = new();

        public int MaxConcurrency { get; private set; }

        public Func<IReadOnlyList<string>, WslRunResult>? Responder { get; init; }

        public Func<IReadOnlyList<string>, Exception>? Thrower { get; init; }

        public async Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            try
            {
                MaxConcurrency = Math.Max(MaxConcurrency, now);
                lock (Calls)
                {
                    Calls.Add(args.ToArray());
                }

                await Task.Yield();
                if (Thrower is not null)
                {
                    throw Thrower(args);
                }

                return Responder?.Invoke(args) ?? new WslRunResult(0, "", "");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
