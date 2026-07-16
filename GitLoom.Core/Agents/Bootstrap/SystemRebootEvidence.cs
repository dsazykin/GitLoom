using System;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Answers "has Windows actually rebooted since moment X?" for the OOBE's reboot gate (audit fix #4).
/// Derived from <see cref="Environment.TickCount64"/> (milliseconds since boot): last boot time =
/// now − uptime. A restart (<c>shutdown /r</c> — the only kind that finalizes the enabled Windows
/// features) always resets the tick counter; a Fast-Startup hybrid shutdown does NOT reset it — and
/// also does NOT process the pending feature operations — so the tick-based answer tracks the truth
/// the OOBE actually cares about ("are the features active yet"), not merely "was the power cycled".
/// </summary>
public static class SystemRebootEvidence
{
    /// <summary>The UTC instant this OS session booted.</summary>
    public static DateTimeOffset LastBootTimeUtc()
        => DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>True when the current OS session started AFTER <paramref name="momentUtc"/> — i.e.
    /// the machine has rebooted since that moment.</summary>
    public static bool RebootedSince(DateTimeOffset momentUtc) => LastBootTimeUtc() > momentUtc;
}
