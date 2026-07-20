using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Regression guard for the adapter-staging pipe deadlock. <see cref="WslRunner"/> feeds file content to
/// an in-distro <c>tee</c> over the child's stdin — and <c>tee</c> echoes that stdin straight back to
/// stdout. Staging an agent CLI streams base64 of a multi-MB tarball, which is far larger than the ~64KB
/// OS pipe buffer, so if the runner writes all of stdin before it starts reading stdout, the child blocks
/// on its stdout write, stops draining stdin, and both sides deadlock until the token kills the process —
/// which reached users as "tee exit -1" / "the pipe is being closed". These tests drive the real runner
/// against a stdin→stdout echoer (POSIX <c>cat</c>, the same relevant behaviour as <c>tee</c>) with a
/// payload well past the buffer; on the pre-fix sequencing they hang and the deadline trips.
/// </summary>
public class WslRunnerStdinTests
{
    // Production WslRunner only ever launches wsl.exe (Windows), but the class runs any executable, and
    // the deadlock is in the OS pipe plumbing, not in wsl.exe. `cat` with no args is a faithful,
    // dependency-free stand-in for the echoing `tee`; it exists on the Linux CI runner (Build & Test is
    // ubuntu-latest) and dev shells. On Windows there is no equivalently trivial stdin echoer, and the
    // production path there goes through wsl.exe regardless, so the body no-ops rather than assert.
    private static bool HaveCat => !OperatingSystem.IsWindows();

    [Fact]
    public async Task RunAsync_ShouldStreamLargeStdin_WithoutDeadlocking()
    {
        if (!HaveCat) return; // see HaveCat — Windows has no trivial stdin echoer; nothing to exercise here.

        // ~11MB of base64 — the shape and scale of a staged CLI tarball, ~170x the pipe buffer.
        var payload = Convert.ToBase64String(new byte[8 * 1024 * 1024]);
        var runner = new WslRunner(executable: "cat");

        // A generous deadline that a healthy run beats by orders of magnitude, but that still FAILS the
        // test (rather than hanging CI forever) if the concurrent-drain fix is ever reverted.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await runner.RunAsync(Array.Empty<string>(), stdin: payload, cts.Token);

        Assert.False(cts.IsCancellationRequested, "the run deadlocked and was killed by the deadline");
        Assert.Equal(0, result.ExitCode);
        // cat echoes stdin verbatim, so the full payload must have round-tripped through the pipe.
        Assert.Equal(payload.Length, result.StdOut.Length);
    }

    [Fact]
    public async Task RunAsync_ShouldStillReturnSmallStdin()
    {
        // The pre-existing small-stdin path (sysctl drop-ins, config shims) must be unaffected.
        if (!HaveCat) return;

        var runner = new WslRunner(executable: "cat");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var result = await runner.RunAsync(Array.Empty<string>(), stdin: "noninteractive=true\n", cts.Token);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("noninteractive=true\n", result.StdOut);
    }
}
