using System;
using Avalonia;

namespace GitLoom.App.Controls;

/// <summary>
/// The pure state machine behind the T-09 drag-to-rebase/merge gesture. It answers one question the
/// canvas can't test comfortably: has a press-and-move on a ref label travelled far enough to count
/// as a <em>drag</em> (rather than a plain click or a right-click)? Keeping the threshold logic here
/// (Avalonia <see cref="Point"/> is the only UI type) means the "click still selects / right-click
/// still opens the menu" contract is unit-testable and not buried in the view code-behind.
/// </summary>
public sealed class LabelDragGesture
{
    /// <summary>Pixels the pointer must travel from the press point before a drag begins (~5px).</summary>
    public const double DefaultThreshold = 5.0;

    private readonly double _threshold;
    private Point _origin;
    private bool _pressed;

    public LabelDragGesture(double threshold = DefaultThreshold)
    {
        if (threshold <= 0) throw new ArgumentOutOfRangeException(nameof(threshold));
        _threshold = threshold;
    }

    /// <summary>The ref name pressed on (the drag source), or null when nothing is armed.</summary>
    public string? SourceRef { get; private set; }

    /// <summary>The commit SHA the source label sits on.</summary>
    public string? SourceSha { get; private set; }

    /// <summary>True once the pointer has moved past the threshold — a real drag is in progress.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>True while a label is pressed (armed) but not yet dragging.</summary>
    public bool IsArmed => _pressed && !IsDragging;

    /// <summary>Arms the gesture on a label press. Movement past the threshold promotes it to a drag.</summary>
    public void Press(Point origin, string refName, string sha)
    {
        _pressed = true;
        IsDragging = false;
        _origin = origin;
        SourceRef = refName;
        SourceSha = sha;
    }

    /// <summary>
    /// Feeds a pointer move. Returns <c>true</c> exactly once — on the move that first crosses the
    /// threshold and promotes the armed press into an active drag. Later moves return <c>false</c>.
    /// </summary>
    public bool Move(Point p)
    {
        if (!_pressed || IsDragging) return false;
        var dx = p.X - _origin.X;
        var dy = p.Y - _origin.Y;
        if ((dx * dx) + (dy * dy) >= _threshold * _threshold)
        {
            IsDragging = true;
            return true;
        }
        return false;
    }

    /// <summary>Clears all state (release, escape, or drop resolved). Safe to call at any time.</summary>
    public void Cancel()
    {
        _pressed = false;
        IsDragging = false;
        SourceRef = null;
        SourceSha = null;
    }
}
