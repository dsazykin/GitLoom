using Avalonia;
using GitLoom.App.Controls;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Pure unit tests for the T-09 drag-gesture threshold state machine (<see cref="LabelDragGesture"/>).
/// The "a plain click still selects / a right-click still opens the menu" contract rides on the
/// threshold: a press that never moves past it must never promote to a drag.
/// </summary>
public class LabelDragGestureTests
{
    [Fact]
    public void SmallMove_DoesNotBeginDrag()
    {
        var g = new LabelDragGesture(threshold: 5.0);
        g.Press(new Point(100, 100), "feature", "abc");

        Assert.False(g.Move(new Point(102, 101)));   // ~2.2px — under threshold
        Assert.False(g.IsDragging);
        Assert.True(g.IsArmed);
    }

    [Fact]
    public void MovePastThreshold_BeginsDrag_Once()
    {
        var g = new LabelDragGesture(threshold: 5.0);
        g.Press(new Point(100, 100), "feature", "abc");

        Assert.True(g.Move(new Point(110, 100)));    // 10px — crosses threshold, begins drag
        Assert.True(g.IsDragging);
        Assert.False(g.IsArmed);
        Assert.False(g.Move(new Point(200, 200)));   // already dragging → returns false thereafter
        Assert.Equal("feature", g.SourceRef);
        Assert.Equal("abc", g.SourceSha);
    }

    [Fact]
    public void Cancel_ClearsState()
    {
        var g = new LabelDragGesture();
        g.Press(new Point(0, 0), "feature", "abc");
        g.Move(new Point(50, 0));
        g.Cancel();

        Assert.False(g.IsDragging);
        Assert.Null(g.SourceRef);
        Assert.Null(g.SourceSha);
        Assert.False(g.Move(new Point(100, 0)));     // no press → no drag
    }
}
