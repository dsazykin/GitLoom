using Mainguard.Agents.Terminal.Vterm;

namespace Mainguard.Server.Terminal;

/// <summary>The terminal target engine behind the P2-18 <c>TerminalEngine=libvterm|interim</c> flag.</summary>
public enum TerminalEngineKind
{
    /// <summary>P2-03 raw byte streaming; the client parses (VtScreen). The default until P2-04
    /// signs the libvterm engine off.</summary>
    Interim = 0,

    /// <summary>P2-18 server-side libvterm: the daemon owns the grid and streams GridUpdate diffs
    /// to grid-capable clients; raw streaming remains for everyone else.</summary>
    Libvterm = 1,
}

/// <summary>
/// The daemon's resolved engine selection, registered as a DI singleton.
/// <see cref="Resolve"/> degrades a libvterm request to interim when the native library cannot
/// load here (Windows local-dev — the libvterm engine is daemon/Linux-only by design), so a
/// misconfigured flag can never take terminals down.
/// </summary>
public sealed record TerminalEngineConfig(TerminalEngineKind Engine)
{
    public static readonly TerminalEngineConfig Interim = new(TerminalEngineKind.Interim);

    /// <summary>Parses the flag value ("libvterm" | "interim", case-insensitive; anything else —
    /// including null — is interim) and applies the native-availability degrade.</summary>
    public static TerminalEngineConfig Resolve(string? flagValue)
    {
        var wantsLibvterm = string.Equals(flagValue, "libvterm", System.StringComparison.OrdinalIgnoreCase);
        return wantsLibvterm && VtermSession.IsSupported
            ? new TerminalEngineConfig(TerminalEngineKind.Libvterm)
            : Interim;
    }
}
