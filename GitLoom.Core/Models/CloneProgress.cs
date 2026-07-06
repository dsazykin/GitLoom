namespace GitLoom.Core.Models;

/// <summary>The stage a clone is in, driving the progress label (T-21).</summary>
public enum ClonePhase
{
    Starting,
    Receiving,
    CheckingOut,
    Completed,
}

/// <summary>
/// One immutable progress snapshot of an in-flight clone (T-21), surfaced to the UI through an
/// <see cref="System.IProgress{T}"/>. <see cref="ReceivedObjects"/> and <see cref="Percent"/> are
/// guaranteed <b>monotonic</b> (never decrease) by <see cref="Services.CloneService"/> so the bar
/// only ever fills forward. Plain data — no repo/IO.
/// </summary>
public sealed record CloneProgress
{
    public ClonePhase Phase { get; init; }

    /// <summary>Objects received so far (libgit2 transfer progress). Monotonic non-decreasing.</summary>
    public int ReceivedObjects { get; init; }

    /// <summary>Total objects the remote advertised (0 until known).</summary>
    public int TotalObjects { get; init; }

    /// <summary>Objects indexed so far.</summary>
    public int IndexedObjects { get; init; }

    /// <summary>Bytes received so far.</summary>
    public long ReceivedBytes { get; init; }

    /// <summary>Checkout steps completed (libgit2 checkout progress).</summary>
    public int CheckoutStep { get; init; }

    /// <summary>Total checkout steps (0 until checkout begins).</summary>
    public int TotalCheckoutSteps { get; init; }

    /// <summary>Overall completion 0–100, monotonic non-decreasing across a clone.</summary>
    public int Percent { get; init; }

    /// <summary>A short human-readable status line for the current phase.</summary>
    public string StatusText { get; init; } = "";
}
