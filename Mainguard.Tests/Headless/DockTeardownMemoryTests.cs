using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.ViewModels.Agents;
using Mainguard.Agents.UI.Views.Agents;
using Mainguard.UI.Theming;
using Xunit;

namespace Mainguard.Tests.Headless;

// P2-13 invariant #1 / test 4 (§5) / TI-P2-13.5 — the BLOCKING memory harness.
//
// The lightweight-by-construction workspace: ONE dock host per screen, whose three panes' content
// swaps as you move between agents (ShowAgent) — the layout and its realized Dock.Avalonia controls
// are reused, never rebuilt. This is deliberate: Dock.Avalonia 11.1.0 retains ~1 MB of realized
// control graph every time a *new* layout is attached to a DockControl (a documented upstream leak),
// so opening/closing agents by newing a workspace each time would leak linearly. Swapping content
// through one host keeps the heap flat across any number of agent switches — the promise the app
// sells on. This test opens/closes an agent workspace 50× the shipping way and asserts a flat heap
// and zero floating dock windows.
public class DockTeardownMemoryTests
{
    [AvaloniaFact]
    public void DockTeardown_50x_MemoryStable()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        using var host = new AgentWorkspaceViewModel("host", WorkspaceLayoutKind.FlightDeck);
        var window = new Window { Width = 900, Height = 640, Content = new AgentWorkspaceView { DataContext = host } };
        window.Show();
        Settle();

        // Warm up the Dock control theme + JIT so the baseline reflects steady state.
        for (int i = 0; i < 3; i++) { host.ShowAgent($"warm-{i}", $"t{i}", $"d{i}", $"s{i}"); Settle(); }
        Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: true);

        // Open/close an agent workspace 50× (the shipping motion: swap the host to another agent).
        for (int i = 0; i < 50; i++)
        {
            host.ShowAgent($"agent-{i}", $"pytest -q  ({i})", $"+ line {i}", $"M src/file{i}.py");
            Settle();

            // No floating dock windows may survive (the documented Dock.Avalonia floating-window leak).
            Assert.True(host.Layout.Windows is null || host.Layout.Windows.Count == 0);
        }

        Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        long growthMb = (after - baseline) / (1024 * 1024);

        window.Content = null;
        window.Close();

        // Content-swap keeps the graph flat; a regression to rebuilding the dock per open would blow past this.
        Assert.True(growthMb < 6, $"Heap grew {growthMb} MB across 50 agent-workspace open/close cycles — a workspace is leaking.");
    }

    [AvaloniaFact]
    public void WorkspaceDispose_ReleasesGraph_NoStaticHandlers()
    {
        // A workspace VM that is created and disposed (never rooted by a live DockControl) must be
        // fully collectable — proof our teardown holds no static strong handlers or timers
        // (WeakReferenceMessenger discipline). The reference is created in a non-inlined frame so the
        // JIT can't keep a stack root alive.
        var weak = CreateAndDisposeWorkspace();
        Collect();
        Assert.False(weak.IsAlive, "A disposed workspace VM was retained — a static handler is rooting it.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDisposeWorkspace()
    {
        var vm = new AgentWorkspaceViewModel("solo", WorkspaceLayoutKind.ConversationDeck);
        var weak = new WeakReference(vm);
        vm.Dispose();
        return weak;
    }

    private static void Collect()
    {
        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(8); }
    }
}
