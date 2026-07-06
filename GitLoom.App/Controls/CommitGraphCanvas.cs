using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using GitLoom.App.Theming;
using GitLoom.Core.Graph;

namespace GitLoom.App.Controls;

public class CommitGraphCanvas : Control
{
    // A dependency property so we can bind the GraphNode from XAML!
    public static readonly StyledProperty<GraphNode> NodeProperty =
        AvaloniaProperty.Register<CommitGraphCanvas, GraphNode>(nameof(Node));

    public GraphNode Node
    {
        get => GetValue(NodeProperty);
        set => SetValue(NodeProperty, value);
    }

    static CommitGraphCanvas()
    {
        AffectsRender<CommitGraphCanvas>(NodeProperty);
    }

    // Lane colors are the Lane1..Lane5 tokens from the active theme dictionary —
    // resolve them from application resources so there is a single source of
    // truth (fallbacks match the Midnight Loom defaults).
    private static readonly (string Key, string Fallback)[] _laneKeys =
    {
        ("Lane1", "#8B8BF5"),
        ("Lane2", "#F472B6"),
        ("Lane3", "#2DD4BF"),
        ("Lane4", "#E3B341"),
        ("Lane5", "#58A6FF")
    };

    private IBrush[]? _laneColorsCache;

    private IBrush[] LaneColors => _laneColorsCache ??= ResolveLaneColors();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnThemeChanged()
    {
        _laneColorsCache = null;
        InvalidateVisual();
    }

    private static IBrush[] ResolveLaneColors()
    {
        var app = Application.Current;
        var brushes = new IBrush[_laneKeys.Length];
        for (int i = 0; i < _laneKeys.Length; i++)
        {
            var (key, fallback) = _laneKeys[i];
            if (app != null
                && app.TryGetResource(key, app.ActualThemeVariant, out var res)
                && res is IBrush brush)
            {
                brushes[i] = brush;
            }
            else
            {
                brushes[i] = SolidColorBrush.Parse(fallback);
            }
        }
        return brushes;
    }

    // Geometry shared with Render(): the dot radius and a comfortable extra slop so a
    // right-click near (not dead-center on) the dot still registers as a Node hit.
    private const double LaneSpacing = 15.0;
    private const double DotRadius = 4.0;
    private const double HitSlop = 6.0;

    /// <summary>
    /// Hit-tests a point (in this control's coordinates) against this row's commit dot using the
    /// pure <see cref="GraphHitTester"/>. Because the graph is one canvas per row, this row is a
    /// single-node graph at RowIndex 0 with no scroll offset. Returned to the timeline so it can
    /// build the correct context menu (T-09). Labels are drawn as chips elsewhere in the row, so
    /// this per-row canvas only ever resolves Node/None.
    /// </summary>
    public GraphHit HitTest(Point p)
    {
        if (Node == null) return new GraphHit(GraphHitKind.None, null, null);
        var tester = new GraphHitTester(
            rowHeight: Bounds.Height <= 0 ? 24.0 : Bounds.Height,
            laneWidth: LaneSpacing,
            nodeRadius: DotRadius,
            hitSlop: HitSlop);
        var nodes = new[] { (RowIndex: 0, LaneIndex: Node.LaneIndex, Sha: Node.CommitSha) };
        return tester.HitTest(p, 0, nodes);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Node == null) return;

        double laneSpacing = 15.0;
        double dotY = Bounds.Height / 2;

        // Draw Top-Half Lines (coming in from the row above)
        foreach (int lane in Node.IncomingLanes)
        {
            var pen = new Pen(LaneColors[lane % LaneColors.Length], 2) { LineCap = PenLineCap.Round };
            double x = (lane * laneSpacing) + (laneSpacing / 2);

            // Draw from top of the row down to the center Y
            context.DrawLine(pen, new Point(x, 0), new Point(x, dotY));
        }

        // Draw Bottom-Half Lines (routing out to parents)
        foreach (var line in Node.OutgoingLines)
        {
            var pen = new Pen(LaneColors[line.ToLane % LaneColors.Length], 2) { LineCap = PenLineCap.Round };

            double fromX = (line.FromLane * laneSpacing) + (laneSpacing / 2);
            double toX = (line.ToLane * laneSpacing) + (laneSpacing / 2);

            // Draw from the center Y down to the bottom of the row
            context.DrawLine(pen, new Point(fromX, dotY), new Point(toX, Bounds.Height));
        }

        // Draw the Commit Dot exactly on top of the converging lines!
        var dotColor = LaneColors[Node.LaneIndex % LaneColors.Length];
        double dotX = (Node.LaneIndex * laneSpacing) + (laneSpacing / 2);

        context.DrawEllipse(dotColor, null, new Point(dotX, dotY), 4, 4);
    }
}
