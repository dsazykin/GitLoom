using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.UI;
using Mainguard.UI.Editions;
using Xunit;

namespace Mainguard.Tests.Headless;

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

    /// <summary>
    /// Seeds <see cref="ViewLocator.ViewAssemblies"/> to the exact set App startup composes for
    /// <paramref name="edition"/> (the shell's own assembly + the edition's contributed View assemblies)
    /// and returns an <see cref="IDisposable"/> that restores the previous value. REQUIRED before a harness
    /// renders shell/Pro content through the App-level <c>&lt;ViewLocator/&gt;</c> DataTemplate: since
    /// ViewLocator moved to Mainguard.UI (step 2c) its bare default is <c>[Mainguard.UI]</c> — which contains
    /// NEITHER the shell Views NOR the Pro Views — so a <c>ContentControl</c> bound to a ViewModel (the agent
    /// rail, control center, workspace, host sections) would otherwise resolve to the "Not Found:" placeholder
    /// (a silent render bug that keeps the build + tests green). Mirrors <c>App.Initialize</c> /
    /// <c>EditionManifestCompletenessTests</c>.
    /// </summary>
    public static IDisposable SeedViewAssemblies(IEditionManifest edition)
    {
        var saved = ViewLocator.ViewAssemblies;
        ViewLocator.ViewAssemblies = Mainguard.App.Shell.App.ComposeViewAssemblies(edition);
        return new Restorer(() => ViewLocator.ViewAssemblies = saved);
    }

    /// <summary>
    /// Fails LOUDLY if the rendered visual tree under <paramref name="root"/> contains any ViewLocator
    /// "Not Found:" placeholder — the automatic catch for a ViewModel that resolved to no View (e.g. a Pro
    /// View absent from <see cref="ViewLocator.ViewAssemblies"/>). Without this, build + tests stay green
    /// while a capture PNG silently paints the placeholder where a real surface should be.
    /// </summary>
    public static void AssertNoUnresolvedViews(Visual root)
    {
        var placeholders = root.GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text)
            .Where(t => t is not null && t.StartsWith("Not Found:", StringComparison.Ordinal))
            .Distinct()
            .ToArray();
        Assert.True(placeholders.Length == 0,
            "ViewLocator rendered placeholder(s) — a ViewModel resolved to no View. Seed " +
            "ViewLocator.ViewAssemblies via HarnessHygiene.SeedViewAssemblies(edition) before rendering. " +
            "Unresolved: " + string.Join(" | ", placeholders));
    }

    private sealed class Restorer : IDisposable
    {
        private readonly Action _restore;
        public Restorer(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }
}
