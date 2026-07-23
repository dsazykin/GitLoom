using System;
using Avalonia.Controls;
using Mainguard.Agents.UI.Controls;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The client half of the P2-18 <c>TerminalEngine=libvterm|interim</c> flag, read from
/// <c>MAINGUARD_TERMINAL_ENGINE</c> (the same variable the daemon honours, so one setting flips
/// both halves in the dev loop). The selection only decides which control the terminal view hosts
/// and whether the attach asks for grid frames — a mismatch with the daemon is safe by
/// construction: a grid-capable client on an interim daemon receives raw frames and simply renders
/// nothing through the grid model, while the daemon protocol handshake (<c>AttachOptions.grid</c>)
/// means an interim client never receives grid frames at all. Default: interim, until the P2-04
/// parity sign-off flips it.
/// </summary>
public static class TerminalEngineSelection
{
    /// <summary>True when this client should render the P2-18 grid engine.</summary>
    public static bool UseGridEngine =>
        string.Equals(
            Environment.GetEnvironmentVariable("MAINGUARD_TERMINAL_ENGINE"),
            "libvterm",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates the engine control the terminal view hosts (flag-dependent).</summary>
    internal static (Control Control, ITerminalEngineControl Engine) CreateEngineControl()
    {
        if (UseGridEngine)
        {
            var grid = new TerminalGridControl();
            return (grid, grid);
        }

        var interim = new TerminalControl();
        return (interim, interim);
    }
}
