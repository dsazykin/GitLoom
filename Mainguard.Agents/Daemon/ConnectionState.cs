namespace Mainguard.Agents.Daemon;

/// <summary>
/// The client-side view of daemon connectivity, surfaced by <c>DaemonClient</c> and
/// rendered by the P2-13 Activity Bar.
/// </summary>
public enum ConnectionState
{
    /// <summary>No live connection (initial, or a terminal auth failure).</summary>
    Down,

    /// <summary>A live, authenticated connection with a healthy event stream.</summary>
    Connected,

    /// <summary>Transiently disconnected; reconnecting with backoff.</summary>
    Degraded,
}
