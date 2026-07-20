using System.Runtime.InteropServices;
using Xunit;

namespace Mainguard.Tests.TestTools;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on Linux, skipping (with a reason, never failing)
/// elsewhere. The forkpty PTY probes are Linux-only by nature; the authoritative run is the
/// Docker/Linux CI leg (P2-03 test-platform reality). Skipping keeps the Windows self-verify green.
/// </summary>
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Skip = "Linux-only PTY test (forkpty). Runs in the Docker/Linux CI leg; skipped on this platform.";
        }
    }
}

/// <summary>
/// A <see cref="FactAttribute"/> that runs only on Windows (ConPTY path), skipping with a reason
/// elsewhere.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Windows-only PTY test (ConPTY). Skipped on this platform.";
        }
    }
}
