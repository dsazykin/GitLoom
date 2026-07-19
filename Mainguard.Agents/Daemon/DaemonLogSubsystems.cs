using System.Collections.Generic;
using System.IO;

using Mainguard.Git;
namespace Mainguard.Agents.Daemon;

/// <summary>
/// The canonical short names of the daemon's log subsystems — one per rolling file at
/// <c>~/.gitloom/logs/&lt;subsystem&gt;.log</c> and the <c>[Subsystem]</c> tag in every journald line.
///
/// <para>Kept in Core (not the server assembly) on purpose: BOTH the App's read surface — the Settings
/// "Daemon logs" panel and <see cref="Agents.Bootstrap.DaemonLogReader"/> — and the Server's category
/// derivation (<c>DaemonLogCategories</c>) share this ONE list, so a subsystem name can never drift
/// between the writer and the reader. A new daemon subsystem (P2-46/P2-49) adds its name here and a
/// matching constant in <c>DaemonLogCategories</c>: the documented extension point.</para>
///
/// <para>This is an explicit name list, not ambient logging — Core itself stays log-free (no
/// <c>ILogger</c> in Core services).</para>
/// </summary>
public static class DaemonLogSubsystems
{
    public const string Lifecycle = "lifecycle";
    public const string Migration = "migration";
    public const string Rpc = "rpc";
    public const string Spawn = "spawn";
    public const string Egress = "egress";
    public const string Gateway = "gateway";
    public const string Terminal = "terminal";
    public const string Merge = "merge";
    public const string Approval = "approval";
    public const string KillSwitch = "killswitch";
    public const string Coordinator = "coordinator";
    public const string Intake = "intake";

    /// <summary>All twelve subsystem names, in canonical order (drives the App's subsystem dropdown).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Lifecycle, Migration, Rpc, Spawn, Egress, Gateway,
        Terminal, Merge, Approval, KillSwitch, Coordinator, Intake,
    };

    /// <summary>The per-subsystem log directory: <c>&lt;DataRoot&gt;/logs</c>. Under
    /// <c>~/.gitloom</c> deliberately — it survives a tier-1 daemon refresh (untouched) and a tier-2
    /// VM upgrade (migrated with <c>.gitloom</c>, minus the logs themselves — see VmUpgrade).</summary>
    public static string LogsDirectory() => Path.Combine(GitLoomPaths.DataRoot(), "logs");
}
