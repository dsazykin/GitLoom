using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.Core;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.App.Services;

/// <summary>
/// The shipped <see cref="IAppStartupEnvironment"/> — the real WSL/DaemonClient side of the startup
/// sequence. It subsumes the old fire-and-forget block (WakeVmInBackground / RefreshDaemonInBackground
/// and its tier-2 + image calls): each seam method is one of those steps, now awaited in order by
/// <see cref="AppStartupSequence"/>. Nothing here throws — every failure is a typed answer or a
/// logged skip — so the sequence's control flow stays deterministic.
/// </summary>
internal sealed class ProductionStartupEnvironment : IAppStartupEnvironment
{
    private static readonly TimeSpan ReachableProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly Action _startKeepAlive;
    private readonly Action<string> _log;

    public ProductionStartupEnvironment(Action startKeepAlive, Action<string> log)
    {
        _startKeepAlive = startKeepAlive ?? throw new ArgumentNullException(nameof(startKeepAlive));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>The loading window's VM — the host for the tier-2 consent/upgrade surface. Set by the
    /// App before the sequence runs (the offer is presented inside the loading screen).</summary>
    public StartupWindowViewModel? Host { get; set; }

    public bool VmUpgradeDeclinedThisSession { get; set; }

    public void Log(string message) => _log(message);

    public void StartKeepAlive() => _startKeepAlive();

    public async Task WakeVmAsync(CancellationToken ct)
    {
        await new WslRunner()
            .RunAsync(WslCommands.InDistro("true"), stdin: null, ct)
            .ConfigureAwait(false);
    }

    public async Task<bool> IsDaemonReachableAsync(CancellationToken ct)
    {
        try
        {
            using var daemon = DaemonClient.ForLoopback();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ReachableProbeTimeout);
            await daemon.GetDaemonInfoAsync(timeout.Token).ConfigureAwait(false);
            return true; // answered
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            return true; // a pre-GetDaemonInfo daemon still ANSWERED — it's reachable
        }
        catch (Exception)
        {
            return false; // unreachable / still booting
        }
    }

    public async Task<DaemonRefreshOutcome> RefreshDaemonAsync(CancellationToken ct)
    {
        var appVersion = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(appVersion))
        {
            return new DaemonRefreshOutcome(
                DaemonRefreshOutcomeKind.Faulted, null, null, "app version unknown — tier-1 skipped");
        }

        DaemonRefreshOutcome? captured = null;
        try
        {
            using var daemon = DaemonClient.ForLoopback();
            await DaemonAutoRefresh.RunAsync(
                appVersion,
                queryDaemonInfo: c => QueryDaemonInfoAsync(daemon, c),
                updater: new DaemonUpdater(new WslRunner()),
                payloadDirectory: DaemonUpdater.DefaultPayloadDirectory(),
                log: _log,
                ct,
                onOutcome: outcome =>
                {
                    captured = outcome;
                    DaemonUpdateToastPublisher.Publish(outcome);
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log($"startup: tier-1 daemon refresh faulted (non-fatal): {ex.Message}");
        }

        return captured
            ?? new DaemonRefreshOutcome(DaemonRefreshOutcomeKind.Faulted, null, null, "no tier-1 outcome");
    }

    public async Task<VmUpgradeAvailability> CheckVmUpgradeAsync(CancellationToken ct)
    {
        using var daemon = DaemonClient.ForLoopback();
        return await VmUpgradeCheck.RunAsync(
            VmUpgradeCheck.DefaultPayloadStampPath(),
            queryDaemonInfo: c => QueryDaemonInfoAsync(daemon, c),
            wsl: new WslRunner(),
            log: _log,
            ct).ConfigureAwait(false);
    }

    public async Task<VmUpgradeDecision> OfferVmUpgradeAsync(VmUpgradeAvailability availability, CancellationToken ct)
    {
        var host = Host;
        if (host is null)
        {
            return VmUpgradeDecision.Declined;
        }

        var tarballPath = Path.Combine(AppContext.BaseDirectory, "payload", "GitLoomOS.tar.gz");
        if (!File.Exists(tarballPath))
        {
            _log($"startup: tier-2 payload {availability.ExpectedVersion} expected but no tarball at "
                + $"'{tarballPath}' — offer skipped");
            return VmUpgradeDecision.Declined;
        }

        var dataRoot = GitLoomPaths.DataRoot();
        var options = new VmUpgradeOptions(
            TarballPath: tarballPath,
            StagingInstallDir: Path.Combine(dataRoot, "vm-staging"),
            CanonicalInstallDir: Path.Combine(dataRoot, "vm"));

        var offer = new VmUpgradeOfferViewModel(
            new VmUpgradeOrchestrator(new WslRunner()),
            options,
            availability.InstalledVersion,
            availability.ExpectedVersion)
        {
            LogSink = _log,
        };

        var decision = new TaskCompletionSource<VmUpgradeDecision>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Later → session flag + CloseAction (below) resolves Declined; the run resolves via
        // the terminal-state watcher.
        offer.Declined = () => VmUpgradeDeclinedThisSession = true;
        offer.CloseAction = () => decision.TrySetResult(VmUpgradeDecision.Declined);
        offer.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(VmUpgradeOfferViewModel.IsComplete) when offer.IsComplete:
                    decision.TrySetResult(VmUpgradeDecision.UpgradedOk);
                    break;
                case nameof(VmUpgradeOfferViewModel.IsRunning) when !offer.IsRunning && !offer.IsOffering && offer.HasError:
                    decision.TrySetResult(VmUpgradeDecision.UpgradeFailed);
                    break;
            }
        };

        await Dispatcher.UIThread.InvokeAsync(() => host.BeginVmUpgrade(offer)).GetTask().ConfigureAwait(false);
        var result = await decision.Task.ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(host.EndVmUpgrade).GetTask().ConfigureAwait(false);
        return result;
    }

    public async Task<System.Collections.Generic.IReadOnlyList<SandboxImageSpec>> ProbeSandboxImagesAsync(
        CancellationToken ct)
    {
        try
        {
            var needs = await new SandboxImageProvisioner(new WslRunner())
                .ProbeNeedsProvisionAsync(ct).ConfigureAwait(false);
            // Missing OR stale — either kicks the background (re)build; the shell surfaces the outcome.
            return needs.Select(n => n.Image).ToArray();
        }
        catch (Exception ex)
        {
            _log($"startup: sandbox image probe failed (non-fatal): {ex.Message}");
            return Array.Empty<SandboxImageSpec>();
        }
    }

    public void KickSandboxImageBuild(System.Collections.Generic.IReadOnlyList<SandboxImageSpec> missing)
    {
        // Fire-and-forget: the (minutes-long) build must never hold the loading screen. Reuses the
        // existing installer so the Installed/Updated/InstallFailed shell toast still fires. It
        // re-probes cheaply; that keeps the toast path single-sourced. The progress sink (previously
        // discarded) now leaves per-step build/load breadcrumbs in oobe.log while it runs.
        var progress = new Progress<string>(line => _log($"sandbox images: {line}"));
        _ = Task.Run(() => SandboxImageInstaller.RunAsync(_log, progress));
    }

    private static async Task<DaemonVersionInfo?> QueryDaemonInfoAsync(DaemonClient daemon, CancellationToken ct)
    {
        try
        {
            return await daemon.GetDaemonInfoAsync(ct).ConfigureAwait(false);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            return null; // pre-GetDaemonInfo daemon — the skew signal itself
        }
    }
}
