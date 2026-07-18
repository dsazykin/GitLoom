using System.Collections.Generic;
using GitLoom.Core.Daemon;

namespace GitLoom.Server.Logging;

/// <summary>
/// The canonical <see cref="Microsoft.Extensions.Logging.ILogger"/> category names for the daemon's
/// subsystems — one per <see cref="DaemonLogSubsystems"/> name, prefixed <c>gitloomd.</c> so a category
/// reads well in the journal (<c>[gitloomd.Spawn]</c>) and <see cref="SubsystemFileLoggerProvider"/> can
/// recover the short subsystem name (the file stem + <c>[tag]</c>) as the last dot-segment lowercased.
///
/// <para>A new daemon subsystem (P2-46 toolchain resolver, P2-49 agent-CLI lifecycle, …) adds one
/// constant here and its short name in <see cref="DaemonLogSubsystems"/>: the documented extension
/// point, so later daemon phases extend this pipeline rather than reinventing observability.</para>
/// </summary>
public static class DaemonLogCategories
{
    private const string Prefix = "gitloomd.";

    public const string Lifecycle = Prefix + "Lifecycle";
    public const string Migration = Prefix + "Migration";
    public const string Rpc = Prefix + "Rpc";
    public const string Spawn = Prefix + "Spawn";
    public const string Egress = Prefix + "Egress";
    public const string Gateway = Prefix + "Gateway";
    public const string Terminal = Prefix + "Terminal";
    public const string Merge = Prefix + "Merge";
    public const string Approval = Prefix + "Approval";
    public const string KillSwitch = Prefix + "KillSwitch";
    public const string Coordinator = Prefix + "Coordinator";
    public const string Intake = Prefix + "Intake";

    /// <summary>Every daemon category, in canonical order. Each maps 1:1 to a
    /// <see cref="DaemonLogSubsystems"/> name via <see cref="Subsystem"/> (a test pins the equivalence).</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Lifecycle, Migration, Rpc, Spawn, Egress, Gateway,
        Terminal, Merge, Approval, KillSwitch, Coordinator, Intake,
    };

    /// <summary>
    /// The short subsystem name (log-file stem + the <c>[tag]</c> in each line) for any category: the
    /// last dot-segment, lowercased. A non-<c>gitloomd</c> category that slips past the framework
    /// filters routes to its own last-segment file rather than crashing the router.
    /// </summary>
    public static string Subsystem(string category)
    {
        if (string.IsNullOrEmpty(category))
            return DaemonLogSubsystems.Lifecycle;

        var lastDot = category.LastIndexOf('.');
        var tail = lastDot >= 0 && lastDot < category.Length - 1 ? category[(lastDot + 1)..] : category;
        return tail.ToLowerInvariant();
    }
}
