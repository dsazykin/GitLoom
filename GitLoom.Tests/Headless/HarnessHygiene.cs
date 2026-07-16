using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace GitLoom.Tests.Headless;

/// <summary>
/// Serial dispatcher hygiene for the [AvaloniaFact] harnesses. Every window a test shows MUST go
/// through <see cref="Teardown"/> before the test ends: the whole assembly shares ONE headless
/// Avalonia session, so a window that stays open keeps its ViewModel alive — including timers
/// (RepoDashboard's 1-minute last-fetched ticker, AutoFetchService, toast auto-dismiss) and
/// fire-and-forget loads — whose later callbacks land inside a DIFFERENT test on the shared
/// dispatcher and poison the session. That is the CI-only "random headless victim /
/// IGlobalClock" failure class: the victim test dies in ~1 ms, and it is never the culprit.
/// Draining alone is not enough — <c>RunJobs()</c> cannot cancel a future timer tick; only
/// disposing the ViewModel does.
/// </summary>
public static class HarnessHygiene
{
    /// <summary>Close the window, dispose its DataContext (stopping VM-owned timers and
    /// background work), and drain the dispatcher so nothing from this test runs in the next.</summary>
    public static void Teardown(Window? win)
    {
        if (win is null) return;
        var dc = win.DataContext;
        win.Close();
        (dc as IDisposable)?.Dispose();
        Dispatcher.UIThread.RunJobs();
    }
}
