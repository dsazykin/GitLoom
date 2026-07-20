namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// A branch/tag chip rendered inline in a commit row (at the ref's tip). Carries the identifiers
/// the T-09 drag gesture and context menus need: <see cref="RefName"/> is what
/// <c>BuildDragActionMenu</c>/<c>BuildRefMenu</c> resolve against; <see cref="Sha"/> is the commit
/// the chip sits on. Pure data — no Avalonia dependency — so row construction stays testable.
/// </summary>
public sealed class RefLabelViewModel
{
    /// <summary>The name merge/rebase/menus resolve against (branch FriendlyName or tag name).</summary>
    public string RefName { get; init; } = string.Empty;

    /// <summary>The short text shown on the chip.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Commit SHA this chip decorates (the ref tip).</summary>
    public string Sha { get; init; } = string.Empty;

    /// <summary>True for a tag chip (neutral styling); false for a branch chip.</summary>
    public bool IsTag { get; init; }

    /// <summary>True when this branch is the checked-out HEAD (emphasized chip).</summary>
    public bool IsCurrentHead { get; init; }
}
