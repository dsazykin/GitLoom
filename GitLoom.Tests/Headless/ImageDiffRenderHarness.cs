using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitLoom.App.Controls;
using GitLoom.App.ViewModels;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-13b image-diff wipe/onion-skin control offscreen. Avalonia headless can't decode
// real PNG bytes, so before/after are SYNTHETIC solid-color WriteableBitmaps (red = before,
// blue = after). At a mid swipe position the wipe boundary lands mid-frame — the left slice shows
// the after (blue) image, the right slice shows the before (red) image, split by the accent divider.
public class ImageDiffRenderHarness
{
    [AvaloniaFact]
    public void Capture_ImageWipe_AtMidPosition()
    {
        var vm = new ImageDiffViewModel
        {
            OldImage = SolidBitmap(160, 120, 220, 60, 60),   // before = red
            NewImage = SolidBitmap(160, 120, 60, 90, 230),   // after  = blue
            OldSize = 4096,
            NewSize = 5120,
            SwipePosition = 0.35,
        };
        var control = new ImageDiffControl { DataContext = vm };
        var win = new Window { Content = control, Width = 520, Height = 460 };
        win.Show();
        Settle();

        Assert.True(vm.HasBothImages);

        // Wipe mode: the after image is clipped at SwipePosition*width — a non-null clip is applied.
        var after = control.GetVisualDescendants().OfType<Image>()
            .First(i => ReferenceEquals(i.Source, vm.NewImage));
        Assert.NotNull(after.Clip);

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "image_diff_wipe.png"));

        // Onion-skin mode: divider hidden, after image blended at SwipePosition opacity.
        vm.IsOnionSkin = true;
        vm.SwipePosition = 0.5;
        Settle();
        Assert.Null(after.Clip);
        Assert.Equal(0.5, after.Opacity, 3);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "image_diff_onion.png"));
    }

    [AvaloniaFact]
    public void PointerDrag_OnDivider_TracksSwipePosition()
    {
        var vm = new ImageDiffViewModel
        {
            OldImage = SolidBitmap(160, 120, 220, 60, 60),
            NewImage = SolidBitmap(160, 120, 60, 90, 230),
            SwipePosition = 0.5,
        };
        var control = new ImageDiffControl { DataContext = vm };
        var win = new Window { Content = control, Width = 520, Height = 460 };
        win.Show();
        Settle();

        var stage = control.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Name == "WipeStage");
        Assert.True(stage.Bounds.Width > 0);

        // Press at ~20% of the stage width, drag to ~75%, release. SwipePosition tracks pointer-X.
        var y = stage.Bounds.Height / 2;
        var pStart = PointOnWindow(stage, win, 0.20 * stage.Bounds.Width, y);
        var pEnd = PointOnWindow(stage, win, 0.75 * stage.Bounds.Width, y);

        win.MouseDown(pStart, MouseButton.Left);
        Settle();
        Assert.InRange(vm.SwipePosition, 0.12, 0.28);

        win.MouseMove(pEnd);
        win.MouseUp(pEnd, MouseButton.Left);
        Settle();
        Assert.InRange(vm.SwipePosition, 0.67, 0.83);
    }

    // A solid-color WriteableBitmap the headless renderer can composite (no PNG decode involved).
    private static WriteableBitmap SolidBitmap(int w, int h, byte r, byte g, byte b)
    {
        var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        var row = new byte[fb.RowBytes];
        for (int x = 0; x < w; x++)
        {
            row[x * 4 + 0] = b;
            row[x * 4 + 1] = g;
            row[x * 4 + 2] = r;
            row[x * 4 + 3] = 255;
        }
        for (int y = 0; y < h; y++)
            Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, fb.RowBytes);
        return bmp;
    }

    private static Point PointOnWindow(Visual v, Visual relativeTo, double x, double y)
        => v.TranslatePoint(new Point(x, y), relativeTo) ?? new Point();

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
