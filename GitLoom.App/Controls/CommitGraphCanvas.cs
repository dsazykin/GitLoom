using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
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

    // Lane colors are the shared branch palette defined in App.axaml — resolve them from
    // application resources so there is a single source of truth (fallbacks match the tokens).
    private static readonly (string Key, string Fallback)[] _laneKeys =
    {
        ("BranchCyan", "#569CD6"),
        ("BranchPink", "#C586C0"),
        ("BranchGreen", "#6A9955"),
        ("BranchRed", "#F44747"),
        ("BranchYellow", "#DCDCAA")
    };

    private IBrush[]? _laneColorsCache;

    private IBrush[] LaneColors => _laneColorsCache ??= ResolveLaneColors();

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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Node == null) return;

        double laneSpacing = 15.0;
        double dotY = Bounds.Height / 2;

        // Draw Top-Half Lines (coming in from the row above)
        foreach (int lane in Node.IncomingLanes)
        {
            var pen = new Pen(LaneColors[lane % LaneColors.Length], 2);
            double x = (lane * laneSpacing) + (laneSpacing / 2);

            // Draw from top of the row down to the center Y
            context.DrawLine(pen, new Point(x, 0), new Point(x, dotY));
        }

        // Draw Bottom-Half Lines (routing out to parents)
        foreach (var line in Node.OutgoingLines)
        {
            var pen = new Pen(LaneColors[line.ToLane % LaneColors.Length], 2);

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
