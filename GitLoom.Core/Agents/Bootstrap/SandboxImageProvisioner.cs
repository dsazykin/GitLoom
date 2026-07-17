using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>One provisionable sandbox image: its docker tag + the name of its bundled source dir
/// (under <c>payload/images/</c> beside the app — the <c>$(GitLoomImageSources)</c> copy step).</summary>
public sealed record SandboxImageSpec(string ImageTag, string SourceDirName);

/// <summary>
/// The two CI-built jail images every real spawn needs (P2-07). Field failure 2026-07-17, twice:
/// they were built in CI and NEVER shipped to installed VMs — a fresh <c>GitLoomEnv</c> import has
/// an empty docker store, and the tier-2 VM upgrade correctly does not migrate it (the image store
/// lives outside <c>/home/gitloom</c>), so the first agent spawn failed on both.
/// </summary>
public static class SandboxImages
{
    /// <summary>The hardened agent jail base image (<c>images/gitloom-agent-base/</c>).</summary>
    public static readonly SandboxImageSpec AgentBase = new("gitloom-agent-base:latest", "gitloom-agent-base");

    /// <summary>The default-deny egress proxy image (<c>images/gitloom-egress-proxy/</c>).</summary>
    public static readonly SandboxImageSpec EgressProxy = new("gitloom-egress-proxy:latest", "gitloom-egress-proxy");

    /// <summary>Both images, in the (serialized) provisioning order.</summary>
    public static IReadOnlyList<SandboxImageSpec> All { get; } = new[] { AgentBase, EgressProxy };
}

/// <summary>
/// Pure argument-list builders for the in-VM image probe/build — the automated form of the manual
/// field unblock (<c>wsl -d GitLoomEnv -- docker build -t &lt;tag&gt;:latest &lt;repo&gt;/images/&lt;name&gt;</c>).
/// Kept separate from the runner (like <see cref="WslCommands"/>/<see cref="DaemonUpdateCommands"/>)
/// so the command shapes — and the G-12 invariant that everything stays scoped in-distro to
/// <c>GitLoomEnv</c> and no builder ever emits the VM-wide shutdown verb — are unit-testable
/// without a process. This is a PROVISIONING-time build; G-16 forbids only agent-RUNTIME builds.
/// </summary>
public static class SandboxImageCommands
{
    /// <summary>Exit 0 iff <paramref name="imageTag"/> is present in the distro's docker store
    /// (<c>--format</c> keeps the success output to the bare image id).</summary>
    public static IReadOnlyList<string> InspectImage(string imageTag) =>
        WslCommands.InDistro("docker", "image", "inspect", "--format", "{{.Id}}", imageTag);

    /// <summary>Builds <paramref name="imageTag"/> from <paramref name="vmSourceDir"/> — the
    /// /mnt-translated form of the bundled Windows source dir.</summary>
    public static IReadOnlyList<string> BuildImage(string imageTag, string vmSourceDir) =>
        WslCommands.InDistro("docker", "build", "-t", imageTag, vmSourceDir);

    /// <summary>Every builder — used by the G-12 unit test to prove none emit the VM-wide shutdown
    /// verb and all stay scoped to <c>GitLoomEnv</c>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBuilders() => new[]
    {
        InspectImage("gitloom-agent-base:latest"),
        BuildImage("gitloom-agent-base:latest", "/mnt/c/Program Files/GitLoom/payload/images/gitloom-agent-base"),
    };
}

/// <summary>How one image's provisioning attempt ended.</summary>
public enum SandboxImageBuildKind
{
    /// <summary>The in-VM <c>docker build</c> succeeded — the image is now in the store.</summary>
    Built,

    /// <summary>The bundled source dir (or its Dockerfile) is absent — skipped, naming the path
    /// (mirrors the daemon-payload "no payload — skipped" posture).</summary>
    SkippedMissingSource,

    /// <summary>The build ran and failed; <see cref="SandboxImageBuildResult.Detail"/> carries the
    /// docker error tail. Never a throw — the next image is still attempted.</summary>
    BuildFailed,
}

/// <summary>One image's typed provisioning outcome (never a bare throw at the caller).</summary>
public sealed record SandboxImageBuildResult(string ImageTag, SandboxImageBuildKind Kind, string Detail);

/// <summary>The sandbox-image provisioning seam (interface-first, per Core convention).</summary>
public interface ISandboxImageProvisioner
{
    /// <summary>Probes the distro's docker store (<c>docker image inspect</c> per image) and returns
    /// which of the <see cref="SandboxImages.All"/> images are missing.</summary>
    Task<IReadOnlyList<SandboxImageSpec>> ProbeMissingAsync(CancellationToken ct);

    /// <summary>Builds each of <paramref name="images"/> in-VM from its bundled source under
    /// <paramref name="bundledImagesRoot"/> (a Windows host path; translated to <c>/mnt/…</c> for
    /// the in-distro build). Builds are SERIALIZED — never two at once — and one image's failure
    /// never stops the next (typed per-image results, never a throw).</summary>
    Task<IReadOnlyList<SandboxImageBuildResult>> ProvisionAsync(
        IReadOnlyList<SandboxImageSpec> images, string bundledImagesRoot,
        Action<string> log, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>
/// Performs v1 provisioning-time sandbox-image installs over the <see cref="IWslRunner"/> seam —
/// argument lists only, never a shell string, everything scoped in-distro to <c>GitLoomEnv</c>
/// (G-12). Backlog approach A: build in the VM from the app-bundled source trees (tiny — Dockerfile
/// + seccomp/config), the automated form of the manual field unblock. <b>Deliberately out of v1
/// scope:</b> image version labels / staleness (skew) detection — presence is the only signal here;
/// the versioning half stays in docs/planning/Agent_Image_Provisioning_And_Daemon_Logging_Backlog.md.
/// </summary>
public sealed class SandboxImageProvisioner : ISandboxImageProvisioner
{
    /// <summary>Generous per-image build budget — the Dockerfiles pin apt/Nix fetches that can take
    /// minutes on a cold cache; a build past this is stuck, not slow.</summary>
    public static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(20);

    /// <summary>Bound on the per-image presence probe (a local docker-API round trip).</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly IWslRunner _wsl;

    public SandboxImageProvisioner(IWslRunner wsl)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
    }

    /// <summary>Where the packaged app ships the image sources (the MSBuild
    /// <c>$(GitLoomImageSources)</c> copy step in GitLoom.App.csproj) — mirrors how
    /// <c>payload/daemon</c> is resolved.</summary>
    public static string DefaultBundledImagesDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "payload", "images");

    /// <summary>The in-VM (<c>/mnt/&lt;drive&gt;/…</c>) form of a Windows source directory — the
    /// path <c>docker build</c> reads inside GitLoomEnv. Pure (reuses <see cref="HostPathTranslator"/>
    /// pinned to the Linux branch; native Linux paths — tests, CI — pass through unchanged).</summary>
    public static string ToVmPath(string hostSourceDirectory) =>
        HostPathTranslator.ToDaemonOpenablePath(hostSourceDirectory, daemonIsWindows: false);

    public async Task<IReadOnlyList<SandboxImageSpec>> ProbeMissingAsync(CancellationToken ct)
    {
        var missing = new List<SandboxImageSpec>();
        foreach (var image in SandboxImages.All)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProbeTimeout);
            var result = await _wsl
                .RunAsync(SandboxImageCommands.InspectImage(image.ImageTag), stdin: null, timeout.Token)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                missing.Add(image);
            }
        }

        return missing;
    }

    public async Task<IReadOnlyList<SandboxImageBuildResult>> ProvisionAsync(
        IReadOnlyList<SandboxImageSpec> images, string bundledImagesRoot,
        Action<string> log, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledImagesRoot);
        ArgumentNullException.ThrowIfNull(log);

        // Strictly serialized: one docker build at a time (two concurrent cold builds would fight
        // over the VM's CPU/network for no gain), and one failure never stops the next image.
        var results = new List<SandboxImageBuildResult>(images.Count);
        foreach (var image in images)
        {
            var result = await BuildOneAsync(image, bundledImagesRoot, progress, ct).ConfigureAwait(false);
            log($"sandbox images: {image.ImageTag} — {result.Kind}: {result.Detail}");
            results.Add(result);
        }

        return results;
    }

    private async Task<SandboxImageBuildResult> BuildOneAsync(
        SandboxImageSpec image, string bundledImagesRoot, IProgress<string>? progress, CancellationToken ct)
    {
        var sourceDir = Path.Combine(bundledImagesRoot, image.SourceDirName);
        if (!File.Exists(Path.Combine(sourceDir, "Dockerfile")))
        {
            return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.SkippedMissingSource,
                $"no bundled source at '{sourceDir}' — skipped");
        }

        var vmSourceDir = ToVmPath(sourceDir);
        progress?.Report($"Building {image.ImageTag} from {vmSourceDir}…");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BuildTimeout);
        try
        {
            var result = await _wsl
                .RunAsync(SandboxImageCommands.BuildImage(image.ImageTag, vmSourceDir), stdin: null, timeout.Token)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.BuildFailed,
                    $"docker build exited {result.ExitCode}: {ErrorTail(result)}");
            }

            progress?.Report($"Built {image.ImageTag}.");
            return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.Built,
                $"built from '{vmSourceDir}'");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation (app shutdown) — not a build outcome
        }
        catch (OperationCanceledException)
        {
            return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.BuildFailed,
                $"docker build timed out after {BuildTimeout.TotalMinutes:0} minutes");
        }
        catch (Exception ex)
        {
            // Per-image isolation: e.g. wsl.exe faults become a typed result, never a throw.
            return new SandboxImageBuildResult(image.ImageTag, SandboxImageBuildKind.BuildFailed, ex.Message);
        }
    }

    /// <summary>The last few meaningful lines of the docker error output — enough to act on,
    /// small enough for one oobe.log breadcrumb.</summary>
    private static string ErrorTail(WslRunResult result)
    {
        var raw = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var lines = raw
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
        return lines.Length == 0 ? "(no docker output)" : string.Join(" | ", lines.TakeLast(5));
    }
}

/// <summary>How one <see cref="SandboxImageAutoProvision.RunAsync"/> attempt ended — the typed form
/// of the oobe.log breadcrumb, for the App's startup toast.</summary>
public enum SandboxImageProvisionOutcomeKind
{
    /// <summary>Both images are already in the VM's docker store; nothing was touched.</summary>
    AllPresent,

    /// <summary>Images are missing but the app ships no bundled sources — skipped.</summary>
    SkippedNoBundledSources,

    /// <summary>Every missing image was built — the first spawn will find them.</summary>
    Installed,

    /// <summary>At least one build failed (details in oobe.log; the spawn preflight names the repair).</summary>
    InstallFailed,

    /// <summary>An unexpected fault in the flow itself (never thrown at the caller).</summary>
    Faulted,
}

/// <summary>One typed provisioning outcome (the callback payload of <see cref="SandboxImageAutoProvision.RunAsync"/>).</summary>
/// <param name="Kind">How the attempt ended.</param>
/// <param name="Detail">The same human-readable text the oobe.log breadcrumb carries.</param>
public sealed record SandboxImageProvisionOutcome(SandboxImageProvisionOutcomeKind Kind, string Detail);

/// <summary>A composed startup-toast payload (proto- and UI-free; the App binds it to its toast host).</summary>
/// <param name="Message">The one-line toast text (Voice Bible pattern T: past tense, names the object).</param>
/// <param name="IsWarning">True for the failed-install warning tone; false for the quiet success pill.</param>
public sealed record SandboxImageToastContent(string Message, bool IsWarning);

/// <summary>
/// The outcome → toast policy: only an attempt that actually CHANGED something (or tried and
/// failed) earns a toast. All-present, no-sources, and internal faults stay silent — a startup
/// pill for "nothing happened" is noise. Pure so the trigger rule is unit-testable without Avalonia.
/// </summary>
public static class SandboxImageToast
{
    public static SandboxImageToastContent? TryCompose(SandboxImageProvisionOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Kind switch
        {
            SandboxImageProvisionOutcomeKind.Installed => new SandboxImageToastContent(
                "Sandbox images installed.", IsWarning: false),
            SandboxImageProvisionOutcomeKind.InstallFailed => new SandboxImageToastContent(
                "Sandbox image install didn't complete — details in oobe.log.", IsWarning: true),
            _ => null,
        };
    }
}

/// <summary>
/// The one call the App makes at control-center startup (in the same background task as the
/// tier-1/tier-2 daemon checks, sequenced after them): probe which jail images the VM's docker
/// store is missing, build any missing one from the bundled sources, log every outcome — and never
/// throw. Silent when nothing is missing; a probe that cannot run (docker/VM down) is a logged
/// skip, and the daemon-side spawn preflight (<c>SandboxImageMissingException</c>) remains the
/// actionable backstop.
/// </summary>
public static class SandboxImageAutoProvision
{
    /// <param name="provisioner">The probe/build performer (fake in tests).</param>
    /// <param name="bundledImagesRoot">The app-shipped image-sources dir (Windows host path).</param>
    /// <param name="log">Outcome breadcrumbs (the App passes its oobe.log writer).</param>
    /// <param name="onOutcome">Optional typed-outcome callback (the App's startup toast). Invoked at
    /// most once, after the outcome is logged, on the caller's thread — never on cancellation, and a
    /// throwing callback is swallowed (this flow must never ripple into the app).</param>
    /// <param name="progress">Optional per-step progress lines (in addition to the log).</param>
    public static async Task RunAsync(
        ISandboxImageProvisioner provisioner,
        string bundledImagesRoot,
        Action<string> log,
        CancellationToken ct,
        Action<SandboxImageProvisionOutcome>? onOutcome = null,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledImagesRoot);
        ArgumentNullException.ThrowIfNull(log);

        // Log first, then report — and never let a throwing callback masquerade as a flow fault.
        void Report(SandboxImageProvisionOutcomeKind kind, string detail)
        {
            try
            {
                onOutcome?.Invoke(new SandboxImageProvisionOutcome(kind, detail));
            }
            catch (Exception)
            {
                // The outcome consumer is cosmetic (a toast); its failure never ripples back.
            }
        }

        try
        {
            var missing = await provisioner.ProbeMissingAsync(ct).ConfigureAwait(false);
            if (missing.Count == 0)
            {
                var present = "sandbox images: "
                    + string.Join(", ", SandboxImages.All.Select(i => i.ImageTag)) + " present — nothing to do";
                log(present);
                Report(SandboxImageProvisionOutcomeKind.AllPresent, present);
                return;
            }

            var missingNames = string.Join(", ", missing.Select(i => i.ImageTag));
            if (!Directory.Exists(bundledImagesRoot))
            {
                var noSources = $"sandbox images: {missingNames} missing but no bundled sources at "
                    + $"'{bundledImagesRoot}' — skipped";
                log(noSources);
                Report(SandboxImageProvisionOutcomeKind.SkippedNoBundledSources, noSources);
                return;
            }

            log($"sandbox images: {missingNames} missing — building from '{bundledImagesRoot}'");
            var results = await provisioner
                .ProvisionAsync(missing, bundledImagesRoot, log, progress, ct).ConfigureAwait(false);

            var failed = results.Where(r => r.Kind == SandboxImageBuildKind.BuildFailed).ToArray();
            if (failed.Length > 0)
            {
                var detail = "sandbox images: install FAILED for "
                    + string.Join(", ", failed.Select(r => r.ImageTag));
                log(detail);
                Report(SandboxImageProvisionOutcomeKind.InstallFailed, detail);
            }
            else if (results.Any(r => r.Kind == SandboxImageBuildKind.Built))
            {
                var detail = "sandbox images: installed "
                    + string.Join(", ", results
                        .Where(r => r.Kind == SandboxImageBuildKind.Built).Select(r => r.ImageTag));
                log(detail);
                Report(SandboxImageProvisionOutcomeKind.Installed, detail);
            }
            else
            {
                // Every missing image was a per-image source skip (partial/empty payload dir).
                var detail = $"sandbox images: {missingNames} missing but their bundled sources are absent — skipped";
                log(detail);
                Report(SandboxImageProvisionOutcomeKind.SkippedNoBundledSources, detail);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutdown mid-provisioning — nothing to log (and no outcome: nobody is listening).
        }
        catch (Exception ex)
        {
            // A failed install must never crash (or even ripple into) the app.
            log($"sandbox images: provisioning FAILED: {ex.Message}");
            Report(SandboxImageProvisionOutcomeKind.Faulted, ex.Message);
        }
    }
}
