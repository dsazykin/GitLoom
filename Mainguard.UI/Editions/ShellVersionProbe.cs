using System;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.UI.Editions;

/// <summary>The in-VM daemon's self-reported versions, projected to primitives so the reference-clean
/// shell's <c>VersionsViewModel</c> can render them without naming the Pro <c>DaemonVersionInfo</c> /
/// <c>DaemonClient</c> types (step 2f). Mirrors the daemon's <c>GetDaemonInfo</c> fields.</summary>
public sealed record DaemonVersionSnapshot(string? DaemonVersion, string? PayloadVersion);

/// <summary>
/// The edition-composition seam for the Settings "About / versions" daemon probe (step 2f). The shell's
/// <c>VersionsViewModel</c> always shows the app's own version; the in-VM daemon / Mainguard OS versions
/// come from an edition with the agent platform, which wires <see cref="Query"/> to its loopback
/// <c>DaemonClient</c> probe. Under the plain Git client this stays <c>null</c> and the daemon/OS rows
/// render as "unreachable" — honest, and reached without the shell referencing any daemon type.
///
/// <para>Contract mirrors the tier-1 auto-refresh probe: a returned snapshot names the versions; a
/// <c>null</c> RESULT means the daemon answered but predates version reporting (Unimplemented); a THROW
/// means unreachable (VM off / daemon down) and the caller maps it to honest "unreachable" text.</para>
/// </summary>
public static class ShellVersionProbe
{
    /// <summary>Set by an edition with the agent platform (its loopback daemon probe); <c>null</c> under
    /// the plain Git client. Follows the static-seam pattern the design system already uses
    /// (<c>ThemeManager.PersistKey</c>) — the base layer never reaches up into a head.</summary>
    public static Func<CancellationToken, Task<DaemonVersionSnapshot?>>? Query { get; set; }
}
