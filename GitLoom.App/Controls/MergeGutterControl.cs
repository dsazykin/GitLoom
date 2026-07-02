using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using GitLoom.Core.Models;

namespace GitLoom.App.Controls;

public class MergeGutterControl : Control
{
    public IEnumerable<MergeChunk>? Chunks { get; set; }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Example: Drawing a semi-transparent polygon connecting chunks
        // var brush = new SolidColorBrush(Color.FromArgb(50, 255, 0, 0));
        // context.DrawGeometry(brush, null, geometry);
    }
}
