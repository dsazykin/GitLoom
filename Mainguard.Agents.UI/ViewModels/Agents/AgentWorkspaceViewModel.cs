using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;

namespace GitLoom.App.ViewModels.Agents;

/// <summary>The two persisted workspace arrangements (P2-13 / <c>UserPreferences.WorkspaceLayout</c>).</summary>
public enum WorkspaceLayoutKind
{
    /// <summary>Terminal fills the left; agent-diff over staging on the right. The default.</summary>
    FlightDeck,
    /// <summary>Terminal spans the top; agent-diff and staging share the bottom row.</summary>
    ConversationDeck,
}

/// <summary>A dock tool whose body is an arbitrary content object (a pane VM), rendered by the
/// <c>AgentWorkspaceView</c> data template. Keeps the Dock model free of view concerns.</summary>
public sealed partial class WorkspaceTool : Tool
{
    [ObservableProperty] private object? _content;
}

/// <summary>
/// Per-agent Dock.Avalonia workspace (P2-13): Terminal + agent-diff + staging as docked panes,
/// arranged by the persisted <see cref="WorkspaceLayoutKind"/>. Owns the teardown discipline the
/// task exists to enforce — <see cref="Dispose"/> closes every floating dock window (the documented
/// Dock.Avalonia leak) and disposes any disposable pane content. Dock.Avalonia lives in the App
/// only; never in Mainguard.Agents.
/// </summary>
public sealed class AgentWorkspaceViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceDockFactory _factory;
    private readonly WorkspaceTool _terminalTool;
    private readonly WorkspaceTool _diffTool;
    private readonly WorkspaceTool _stagingTool;
    private bool _disposed;

    /// <summary>The agent currently shown in this workspace host.</summary>
    public string AgentId { get; private set; }

    public WorkspaceLayoutKind LayoutKind { get; }

    /// <summary>The Dock root the <c>DockControl</c> binds to.</summary>
    public IRootDock Layout { get; }

    public AgentWorkspaceViewModel(
        string agentId,
        WorkspaceLayoutKind layout = WorkspaceLayoutKind.FlightDeck,
        object? terminal = null,
        object? diff = null,
        object? staging = null)
    {
        AgentId = agentId;
        LayoutKind = layout;

        _terminalTool = new WorkspaceTool { Id = "terminal", Title = "Terminal", Content = terminal ?? "Terminal", CanClose = false };
        _diffTool = new WorkspaceTool { Id = "diff", Title = "Agent diff", Content = diff ?? "Agent diff (read-only)", CanClose = false };
        _stagingTool = new WorkspaceTool { Id = "staging", Title = "Staging", Content = staging ?? "Staging", CanClose = false };

        _factory = new WorkspaceDockFactory(_terminalTool, _diffTool, _stagingTool, layout);
        Layout = _factory.CreateLayout();
        _factory.InitLayout(Layout);
    }

    /// <summary>
    /// Point this ONE workspace host at a different agent by swapping the three panes' content —
    /// the layout (and its realized Dock controls) is reused, never rebuilt. This is the lightweight
    /// switching path: opening another agent costs three content swaps, not a fresh dock graph, so
    /// the heap stays flat no matter how many agents you cycle through. Disposes replaced content
    /// that owns resources.
    /// </summary>
    public void ShowAgent(string agentId, object? terminal, object? diff, object? staging)
    {
        AgentId = agentId;
        SwapContent(_terminalTool, terminal ?? "Terminal");
        SwapContent(_diffTool, diff ?? "Agent diff (read-only)");
        SwapContent(_stagingTool, staging ?? "Staging");
    }

    private static void SwapContent(WorkspaceTool tool, object? next)
    {
        if (ReferenceEquals(tool.Content, next)) return;
        (tool.Content as IDisposable)?.Dispose();
        tool.Content = next;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Close floating dock windows FIRST — the documented Dock.Avalonia leak this task owns.
        try
        {
            if (Layout.Windows is { } windows)
                foreach (var w in windows.ToList())
                    try { w.Exit(); } catch { /* best effort teardown */ }
        }
        catch { /* ignore */ }

        try
        {
            foreach (var hostWindow in _factory.HostWindows.ToList())
                try { hostWindow.Exit(); } catch { /* best effort teardown */ }
        }
        catch { /* ignore */ }

        try { _factory.CloseAllDockables(Layout); } catch { /* ignore */ }

        foreach (var tool in new[] { _terminalTool, _diffTool, _stagingTool })
        {
            (tool.Content as IDisposable)?.Dispose();
            tool.Content = null;
        }

        // Break the Dock control registries so the DockControl + its visual tree can be collected
        // (the factory otherwise roots them for the process lifetime — the retained-graph leak).
        TryClear(_factory.DockControls);
        TryClear(_factory.HostWindows);
        TryClearDict(_factory.VisibleDockableControls);
        TryClearDict(_factory.TabDockableControls);
        TryClearDict(_factory.PinnedDockableControls);
        try { Layout.VisibleDockables?.Clear(); } catch { /* ignore */ }
    }

    private static void TryClear<T>(System.Collections.Generic.IList<T>? list)
    {
        try { list?.Clear(); } catch { /* ignore */ }
    }

    private static void TryClearDict<TKey, TValue>(System.Collections.Generic.IDictionary<TKey, TValue>? dict)
    {
        try { dict?.Clear(); } catch { /* ignore */ }
    }
}

/// <summary>Builds the two persisted dock arrangements. Internal to the App.</summary>
internal sealed class WorkspaceDockFactory : Factory
{
    private readonly WorkspaceTool _terminal;
    private readonly WorkspaceTool _diff;
    private readonly WorkspaceTool _staging;
    private readonly WorkspaceLayoutKind _kind;

    public WorkspaceDockFactory(WorkspaceTool terminal, WorkspaceTool diff, WorkspaceTool staging, WorkspaceLayoutKind kind)
    {
        _terminal = terminal;
        _diff = diff;
        _staging = staging;
        _kind = kind;
    }

    public override IRootDock CreateLayout()
    {
        var terminalDock = ToolDockFor(_terminal, "TerminalDock", 0.55);
        var diffDock = ToolDockFor(_diff, "DiffDock", 0.6);
        var stagingDock = ToolDockFor(_staging, "StagingDock", 0.4);

        IDock main;
        if (_kind == WorkspaceLayoutKind.ConversationDeck)
        {
            // Terminal spans the top; diff + staging share the bottom row.
            var bottomRow = new ProportionalDock
            {
                Orientation = Orientation.Horizontal,
                Proportion = 0.4,
                VisibleDockables = CreateList<IDockable>(diffDock, new ProportionalDockSplitter(), stagingDock),
            };
            terminalDock.Proportion = 0.6;
            main = new ProportionalDock
            {
                Orientation = Orientation.Vertical,
                VisibleDockables = CreateList<IDockable>(terminalDock, new ProportionalDockSplitter(), bottomRow),
            };
        }
        else
        {
            // Flight Deck (default): terminal on the left; diff over staging on the right.
            var rightColumn = new ProportionalDock
            {
                Orientation = Orientation.Vertical,
                Proportion = 0.45,
                VisibleDockables = CreateList<IDockable>(diffDock, new ProportionalDockSplitter(), stagingDock),
            };
            main = new ProportionalDock
            {
                Orientation = Orientation.Horizontal,
                VisibleDockables = CreateList<IDockable>(terminalDock, new ProportionalDockSplitter(), rightColumn),
            };
        }

        var root = CreateRootDock();
        root.Id = "WorkspaceRoot";
        root.Title = "Workspace";
        root.VisibleDockables = CreateList<IDockable>(main);
        root.ActiveDockable = main;
        root.DefaultDockable = main;
        return root;
    }

    private ToolDock ToolDockFor(WorkspaceTool tool, string id, double proportion) => new()
    {
        Id = id,
        Title = tool.Title,
        Proportion = proportion,
        VisibleDockables = CreateList<IDockable>(tool),
        ActiveDockable = tool,
    };
}
