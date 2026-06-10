using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
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

    private readonly SolidColorBrush[] _laneColors =
    {
        SolidColorBrush.Parse("#569CD6"), // BranchCyan
        SolidColorBrush.Parse("#C586C0"), // BranchPink
        SolidColorBrush.Parse("#6A9955"), // BranchGreen
        SolidColorBrush.Parse("#F44747"), // BranchRed
        SolidColorBrush.Parse("#DCDCAA")  // Yellow
    };

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Node == null) return;

        double laneSpacing = 15.0;
        double dotY = Bounds.Height / 2;

        // Draw Top-Half Lines (coming in from the row above)
        foreach (int lane in Node.IncomingLanes)
        {
            var pen = new Pen(_laneColors[lane % _laneColors.Length], 2);
            double x = (lane * laneSpacing) + (laneSpacing / 2);

            // Draw from top of the row down to the center Y
            context.DrawLine(pen, new Point(x, 0), new Point(x, dotY));
        }

        // Draw Bottom-Half Lines (routing out to parents)
        foreach (var line in Node.OutgoingLines)
        {
            var pen = new Pen(_laneColors[line.ToLane % _laneColors.Length], 2);

            double fromX = (line.FromLane * laneSpacing) + (laneSpacing / 2);
            double toX = (line.ToLane * laneSpacing) + (laneSpacing / 2);

            // Draw from the center Y down to the bottom of the row
            context.DrawLine(pen, new Point(fromX, dotY), new Point(toX, Bounds.Height));
        }

        // Draw the Commit Dot exactly on top of the converging lines!
        var dotColor = _laneColors[Node.LaneIndex % _laneColors.Length];
        double dotX = (Node.LaneIndex * laneSpacing) + (laneSpacing / 2);

        context.DrawEllipse(dotColor, null, new Point(dotX, dotY), 4, 4);
    }
}