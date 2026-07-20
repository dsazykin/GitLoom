using System.Runtime.InteropServices;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>Runs only on Linux (forkpty), skipping with a reason elsewhere.</summary>
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

/// <summary>Runs only on Windows (ConPTY), skipping with a reason elsewhere.</summary>
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
