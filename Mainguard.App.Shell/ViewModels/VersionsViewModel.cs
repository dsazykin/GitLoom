using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.App.Shell.Editions;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// The read-only versions surface in the Settings window (File → Settings… → "About / versions"):
/// the app's own informational version plus the in-VM daemon's version and the MainguardOS payload
/// version, both from the tier-1 <c>GetDaemonInfo</c> probe. Fetches once when the window opens
/// (the view's <c>Opened</c> hook) and again on the manual Refresh button — never a polling loop.
/// Daemon-down and pre-<c>GetDaemonInfo</c> daemons render as honest text ("unreachable",
/// "pre-0.2.0"), never a crash or a blank.
///
/// <para>Constructed directly (no DI); the <c>GetDaemonInfo</c> query is an injectable seam
/// (identical shape to <see cref="DaemonAutoRefresh.RunAsync"/>'s: <c>null</c> means the daemon
/// answered <c>Unimplemented</c>, a throw means unreachable) so the VM is unit-testable offline.</para>
/// </summary>
public partial class VersionsViewModel : ViewModelBase
{
    /// <summary>Value shown before the first fetch has answered.</summary>
    internal const string CheckingText = "checking…";

    /// <summary>Shown when the daemon never answered (VM off / mainguardd down).</summary>
    internal const string UnreachableText = "unreachable — is Mainguard OS running?";

    /// <summary>Shown for a daemon that predates the <c>GetDaemonInfo</c> RPC (pre-0.2.0).</summary>
    internal const string PreRpcDaemonText = "pre-0.2.0 (predates version reporting)";

    /// <summary>Shown when the reached daemon carries no /etc/mainguardos-release stamp.</summary>
    internal const string UnstampedPayloadText = "not stamped";

    /// <summary>Shown for the payload when the daemon itself cannot report versions.</summary>
    internal const string UnknownPayloadText = "unknown (daemon predates version reporting)";

    /// <summary>The daemon-version probe (step 2f). Wired by an edition with the agent platform to its
    /// loopback <c>DaemonClient</c> (via <see cref="ShellVersionProbe.Query"/>); <c>null</c> under the plain
    /// Git client, where there is no in-VM daemon and the daemon/OS rows render "unreachable".</summary>
    private readonly Func<CancellationToken, Task<DaemonVersionSnapshot?>>? _queryDaemonInfo;

    /// <summary>This app's own informational version — known statically, never fetched.</summary>
    public string AppVersion { get; }

    [ObservableProperty]
    private string _daemonVersion = CheckingText;

    [ObservableProperty]
    private string _osVersion = CheckingText;

    [ObservableProperty]
    private bool _isRefreshing;

    /// <summary>Production wiring: the edition's daemon-version probe seam (<see cref="ShellVersionProbe.Query"/>).
    /// Under an edition with the agent platform this is the loopback <c>DaemonClient</c> probe (created per
    /// fetch, cheap, mirroring the auto-refresh path); under the plain Git client it is <c>null</c> and the
    /// daemon/OS rows render "unreachable".</summary>
    public VersionsViewModel()
        : this(ShellVersionProbe.Query)
    {
    }

    public VersionsViewModel(
        Func<CancellationToken, Task<DaemonVersionSnapshot?>>? queryDaemonInfo, string? appVersion = null)
    {
        _queryDaemonInfo = queryDaemonInfo;
        AppVersion = appVersion
            ?? typeof(VersionsViewModel).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
    }

    /// <summary>One fetch: probe <c>GetDaemonInfo</c> and map the three honest states. Never throws.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsRefreshing)
        {
            return;
        }

        IsRefreshing = true;
        DaemonVersion = CheckingText;
        OsVersion = CheckingText;
        try
        {
            if (_queryDaemonInfo is null)
            {
                // No agent platform (the plain Git client) — there is no in-VM daemon to ask.
                DaemonVersion = UnreachableText;
                OsVersion = UnreachableText;
                return;
            }

            var info = await _queryDaemonInfo(CancellationToken.None).ConfigureAwait(true);
            if (info is null)
            {
                // The daemon answered Unimplemented — alive, but too old to name itself.
                DaemonVersion = PreRpcDaemonText;
                OsVersion = UnknownPayloadText;
            }
            else
            {
                DaemonVersion = string.IsNullOrWhiteSpace(info.DaemonVersion)
                    ? PreRpcDaemonText
                    : info.DaemonVersion;
                OsVersion = string.IsNullOrWhiteSpace(info.PayloadVersion)
                    ? UnstampedPayloadText
                    : info.PayloadVersion;
            }
        }
        catch (Exception)
        {
            // Daemon down / VM off — an expected state, shown honestly, never rethrown.
            DaemonVersion = UnreachableText;
            OsVersion = UnreachableText;
        }
        finally
        {
            IsRefreshing = false;
        }
    }
}
