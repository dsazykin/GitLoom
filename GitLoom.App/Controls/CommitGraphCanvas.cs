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

        // Space each lane 15 pixels apart
        double laneSpacing = 15.0;

        // Find the X coordinate for this node's dot
        double dotX = (Node.LaneIndex * laneSpacing) + (laneSpacing / 2);
        double dotY = Bounds.Height / 2;

        // Pick a color based on the LaneIndex (looping if there are many lanes)
        var color = _laneColors[Node.LaneIndex % _laneColors.Length];

        // Draw the dot!
        context.DrawEllipse(color, null, new Point(dotX, dotY), 4, 4);
    }
}