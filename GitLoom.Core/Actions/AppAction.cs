using System;
using System.Threading.Tasks;

namespace GitLoom.Core.Actions;

/// <summary>
/// A single invokable command surfaced by the command palette and the global keyboard shortcuts (T-18).
/// UI-free by design: <see cref="Execute"/> is a delegate the App layer supplies (usually wrapping an
/// existing <c>[RelayCommand]</c>), so this type — and the whole registry/matcher/shortcut trio — later
/// becomes the agent command surface without dragging in any UI dependency.
/// </summary>
public sealed class AppAction
{
    /// <summary>Stable, unique identifier (e.g. <c>commit</c>). Duplicate ids throw on registration.</summary>
    public string Id { get; init; } = "";

    /// <summary>Human-facing label shown in the palette (e.g. "Commit").</summary>
    public string Title { get; init; } = "";

    /// <summary>Grouping bucket shown in the palette (e.g. "Repository", "Branch", "View").</summary>
    public string Category { get; init; } = "";

    /// <summary>
    /// Availability predicate. Actions that fail this are filtered out of <see cref="ActionRegistry.Enabled"/>
    /// (never shown-then-crash on invoke). Evaluated live, so an action needing an open repo returns false
    /// when none is open.
    /// </summary>
    public Func<bool> CanExecute { get; init; } = () => true;

    /// <summary>The work to run when the action is invoked.</summary>
    public Func<Task> Execute { get; init; } = () => Task.CompletedTask;
}
