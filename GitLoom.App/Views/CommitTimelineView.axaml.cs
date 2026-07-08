using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitLoom.App.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class CommitTimelineView : UserControl
{
    private ListBox? _commitsListBox;

    // Drag-to-rebase/merge gesture (T-09 §3.3). The gesture state machine arms on a chip press and
    // promotes to a drag once the pointer passes the threshold; the overlay carries the ghost label.
    private readonly LabelDragGesture _labelDrag = new();
    private Canvas? _dragOverlay;
    private Border? _dragGhost;
    private TextBlock? _dragGhostText;
    private Border? _dropTarget;

    // Reused per drag-move to resolve which chip sits under the cursor (label rects → GraphHit).
    private readonly GraphHitTester _labelHitTester = new(rowHeight: 24, laneWidth: 15, nodeRadius: 4, hitSlop: 0);

    public CommitTimelineView()
    {
        InitializeComponent();

        this.AttachedToVisualTree += (s, e) =>
        {
            _commitsListBox = this.FindControl<ListBox>("CommitsListBox");
            _dragOverlay = this.FindControl<Canvas>("DragOverlay");
            _dragGhost = this.FindControl<Border>("DragGhost");
            _dragGhostText = this.FindControl<TextBlock>("DragGhostText");
            if (_commitsListBox != null)
            {
                _commitsListBox.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);
                _commitsListBox.PointerPressed += OnListBoxPointerPressed;
                _commitsListBox.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
                _commitsListBox.KeyDown += OnListBoxKeyDown;
                // Drag gesture: press/move/release. handledEventsToo so ListBoxItem selection (which
                // handles PointerPressed) doesn't swallow the arm, and capture keeps moves flowing.
                _commitsListBox.AddHandler(PointerPressedEvent, OnLabelPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                _commitsListBox.AddHandler(PointerMovedEvent, OnLabelPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                _commitsListBox.AddHandler(PointerReleasedEvent, OnLabelPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            }
        };
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is CommitTimelineViewModel vm)
        {
            var scrollViewer = e.Source as ScrollViewer;
            if (scrollViewer != null)
            {
                if (scrollViewer.Offset.Y > 0 && scrollViewer.Offset.Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 500)
                {
                    vm.LoadMoreCommits();
                }
            }
        }
    }

    // Delete key on a selected ref label → delete the branch (through the confirmation dialog),
    // T-09 §3.5. SelectedRefName is armed when a label context menu is opened. Escape cancels a drag.
    private void OnListBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _labelDrag.IsDragging)
        {
            CancelLabelDrag();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Delete
            && DataContext is CommitTimelineViewModel vm
            && !string.IsNullOrEmpty(vm.SelectedRefName)
            && vm.DeleteSelectedRefCommand.CanExecute(null))
        {
            vm.DeleteSelectedRefCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var control = e.Source as Control;
        if (control != null && control.DataContext is CommitTimelineViewModel vm)
        {
            vm.SelectedCommit = null;
        }
    }

    // --- Drag-to-rebase/merge pointer gesture (T-09 §3.3) -----------------------------------

    // Press on a ref chip arms the gesture (records source ref + press point). We do NOT handle the
    // event, so a plain click still selects the row and a right-click still opens the context menu —
    // a drag only begins once the pointer travels past the threshold in OnLabelPointerMoved.
    private void OnLabelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_commitsListBox is null) return;
        var props = e.GetCurrentPoint(_commitsListBox).Properties;
        // Only a left press arms a drag (right-click still opens the context menu). Middle/right
        // presses never arm; a left (or synthetic no-button) press over a chip does.
        if (props.IsRightButtonPressed || props.IsMiddleButtonPressed) return;

        // Point-based hit-test (reuses the label rects) is robust to which inner visual e.Source is.
        var pressPoint = e.GetPosition(_commitsListBox);
        var (refName, border) = HitTestRefChip(pressPoint);
        if (refName is not null && border?.DataContext is RefLabelViewModel label)
        {
            _labelDrag.Press(pressPoint, label.RefName, label.Sha);
            return;
        }

        // Not a chip — arm a commit-source drag when the press lands on the graph column (#87).
        // SourceRef is the empty-string sentinel that distinguishes a commit drag from a label
        // drag downstream (OnLabelPointerReleased); LabelDragGesture requires a non-null refName.
        if (e.Source is Control control
            && control.FindAncestorOfType<CommitGraphCanvas>(includeSelf: true) is not null
            && FindRowViewModel(control) is { } row)
        {
            _labelDrag.Press(pressPoint, string.Empty, row.Commit.Sha);
        }
    }

    private void OnLabelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_commitsListBox is null || _labelDrag.SourceRef is null) return;

        var p = e.GetPosition(_commitsListBox);

        if (_labelDrag.Move(p))
        {
            // Threshold crossed → begin the drag: capture the pointer, raise the ghost.
            e.Pointer.Capture(_commitsListBox);
            if (_dragGhost != null && _dragGhostText != null)
            {
                // Commit drag (#87): SourceRef is the empty-string sentinel; show the short SHA instead.
                _dragGhostText.Text = _labelDrag.SourceRef.Length > 0
                    ? _labelDrag.SourceRef
                    : _labelDrag.SourceSha is { Length: >= 7 } sha ? sha.Substring(0, 7) : _labelDrag.SourceSha;
                _dragGhost.IsVisible = true;
            }
        }

        if (!_labelDrag.IsDragging) return;

        // Follow the cursor and highlight the chip under it (excluding the source).
        if (_dragGhost != null)
        {
            Canvas.SetLeft(_dragGhost, p.X + 10);
            Canvas.SetTop(_dragGhost, p.Y + 8);
        }
        UpdateDropTarget(p);
        e.Handled = true;
    }

    private void OnLabelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_commitsListBox is null) return;
        if (!_labelDrag.IsDragging)
        {
            // Not a drag — leave click/select/right-click untouched.
            _labelDrag.Cancel();
            return;
        }

        var source = _labelDrag.SourceRef;
        var sourceSha = _labelDrag.SourceSha;
        var target = HitTestRefLabel(e.GetPosition(_commitsListBox));
        var dropAnchor = _dropTarget;

        CancelLabelDrag();
        e.Handled = true;

        if (DataContext is not CommitTimelineViewModel vm) return;

        // Commit dropped onto a chip (#87) vs. the existing chip-onto-chip drag — same drop-target
        // hit-test, different resolver/flyout (Reset/Rebase-onto-commit vs. Merge/Rebase-refs).
        var items = string.IsNullOrEmpty(source)
            ? vm.ResolveCommitDrop(sourceSha, target)
            : vm.ResolveLabelDrop(source, target);
        if (items is null || items.Count == 0) return;

        var menu = BuildContextMenu(items);
        try
        {
            menu.Open((Control?)dropAnchor ?? _commitsListBox);
        }
        catch
        {
            // Headless/detached top-level may reject Open(); the resolve is already recorded.
        }
    }

    // Highlights the chip under the cursor as the drop target (skips the source chip).
    private void UpdateDropTarget(Point p)
    {
        var (target, border) = HitTestRefChip(p);
        if (border == _dropTarget) return;

        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = (target != null && target != _labelDrag.SourceRef) ? border : null;
        _dropTarget?.Classes.Add("dropTarget");
    }

    // Resolves the ref name under a point using the pure GraphHitTester (label rects → Label hit).
    private string? HitTestRefLabel(Point p) => HitTestRefChip(p).RefName;

    private (string? RefName, Border? Border) HitTestRefChip(Point p)
    {
        if (_commitsListBox is null) return (null, null);

        var chips = EnumerateRefChips().ToList();
        var frame = new List<(Rect, string, string)>();
        foreach (var (border, label) in chips)
        {
            var topLeft = border.TranslatePoint(new Point(0, 0), _commitsListBox);
            if (topLeft is null) continue;
            frame.Add((new Rect(topLeft.Value, border.Bounds.Size), label.RefName, label.Sha));
        }

        _labelHitTester.SetLabelBounds(frame);
        var hit = _labelHitTester.HitTest(p, 0, System.Array.Empty<(int, int, string)>());
        if (hit.Kind != GraphHitKind.Label || hit.RefName is null) return (null, null);

        var match = chips.FirstOrDefault(c => c.Label.RefName == hit.RefName);
        return (hit.RefName, match.Border);
    }

    private IEnumerable<(Border Border, RefLabelViewModel Label)> EnumerateRefChips()
    {
        if (_commitsListBox is null) yield break;
        foreach (var border in _commitsListBox.GetVisualDescendants().OfType<Border>())
        {
            if (border.Classes.Contains("RefChip") && border.DataContext is RefLabelViewModel label)
            {
                yield return (border, label);
            }
        }
    }

    private void CancelLabelDrag()
    {
        _labelDrag.Cancel();
        _dropTarget?.Classes.Remove("dropTarget");
        _dropTarget = null;
        if (_dragGhost != null) _dragGhost.IsVisible = false;
    }

    private static Border? FindRefChip(Control? control)
    {
        while (control is not null)
        {
            if (control is Border b && b.Classes.Contains("RefChip")) return b;
            control = control.GetVisualParent() as Control;
        }
        return null;
    }

    // Right-click on a commit row: run the pure hit-tester when the pointer is over the graph
    // canvas (Node vs. Label), otherwise treat the whole row as a commit hit. The menu itself is
    // built by the ViewModel (context rules live there, testably); this only renders it.
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not CommitTimelineViewModel vm) return;
        if (e.Source is not Control source) return;

        // Right-click on a ref chip → the label menu (pin/unpin, delete, tag/branch actions).
        var chip = FindRefChip(source);
        if (chip?.DataContext is RefLabelViewModel chipLabel)
        {
            var labelItems = vm.BuildContextMenuForHit(new GraphHit(GraphHitKind.Label, chipLabel.Sha, chipLabel.RefName));
            if (labelItems is { Count: > 0 })
            {
                BuildContextMenu(labelItems).Open(source);
            }
            e.Handled = true;
            return;
        }

        var rowVm = FindRowViewModel(source);
        if (rowVm is null) return;

        GraphHit hit;
        var canvas = source.FindAncestorOfType<CommitGraphCanvas>(includeSelf: true);
        if (canvas is not null && e.TryGetPosition(canvas, out var pos))
        {
            hit = canvas.HitTest(pos);
        }
        else
        {
            hit = new GraphHit(GraphHitKind.Node, rowVm.Commit.Sha, null);
        }

        // Node/Label → its menu; empty graph space still yields the row's commit menu.
        var items = vm.BuildContextMenuForHit(hit) ?? vm.BuildCommitMenu(rowVm.Commit.Sha);
        if (items.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var menu = BuildContextMenu(items);
        menu.Open(source);
        e.Handled = true;
    }

    private static CommitRowViewModel? FindRowViewModel(Control? control)
    {
        while (control is not null)
        {
            if (control.DataContext is CommitRowViewModel row) return row;
            control = control.GetVisualParent() as Control;
        }
        return null;
    }

    private static ContextMenu BuildContextMenu(ObservableCollection<MenuItemViewModel> items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
        {
            menu.Items.Add(BuildMenuNode(item));
        }
        return menu;
    }

    private static Control BuildMenuNode(MenuItemViewModel vm)
    {
        if (vm is SeparatorViewModel)
        {
            return new Separator();
        }

        var menuItem = new MenuItem
        {
            Header = vm.Header,
            Command = vm.Command,
            CommandParameter = vm.CommandParameter,
            IsEnabled = vm.IsEnabled
        };

        foreach (var child in vm.SubItems)
        {
            menuItem.Items.Add(BuildMenuNode(child));
        }

        return menuItem;
    }
}
