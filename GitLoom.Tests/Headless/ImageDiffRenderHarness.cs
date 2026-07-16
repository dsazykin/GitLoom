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
using Avalonia.Media;
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
// Coverage: equal-size wipe + onion crossfade; an unequal-size (different-aspect) case asserting the
// wipe boundary + both clips are computed against the letterboxed rendered image rect (not the raw
// control width); and a pointer drag that tracks SwipePosition across that rendered rect.
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

        var before = control.GetVisualDescendants().OfType<Image>()
            .First(i => ReferenceEquals(i.Source, vm.OldImage));

        // Onion-skin mode: divider hidden, true crossfade — before opacity 1-pos, after opacity pos.
        vm.IsOnionSkin = true;
        vm.SwipePosition = 0.5;
        Settle();
        Assert.Null(after.Clip);
        Assert.Null(before.Clip);
        Assert.Equal(0.5, after.Opacity, 3);
        Assert.Equal(0.5, before.Opacity, 3);   // <- the crossfade fix: old image is NOT fully opaque
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "image_diff_onion.png"));

        // At full reveal only the after shows: before has faded completely out.
        vm.SwipePosition = 1.0;
        Settle();
        Assert.Equal(1.0, after.Opacity, 3);
        Assert.Equal(0.0, before.Opacity, 3);
        HarnessHygiene.Teardown(win);
    }

    // Bug #2/#3 regression: with an unequal-size before/after (different aspect ratios), the wipe
    // boundary + clips must be computed against the letterboxed *rendered image rect*, not the raw
    // control width — otherwise the divider drifts off the image edge and the old image bleeds.
    [AvaloniaFact]
    public void Wipe_UnequalSizes_ClipsToRenderedImageRect()
    {
        var vm = new ImageDiffViewModel
        {
            OldImage = SolidBitmap(200, 120, 220, 60, 60),   // before = wide red
            NewImage = SolidBitmap(120, 200, 60, 90, 230),   // after  = tall blue (different aspect)
            OldSize = 4096,
            NewSize = 5120,
            SwipePosition = 0.5,
        };
        var control = new ImageDiffControl { DataContext = vm };
        var win = new Window { Content = control, Width = 520, Height = 460 };
        win.Show();
        Settle();

        var stage = control.GetVisualDescendants().OfType<Panel>()
            .First(p => p.Name == "WipeStage");
        Assert.True(stage.Bounds.Width > 0 && stage.Bounds.Height > 0);

        var after = control.GetVisualDescendants().OfType<Image>()
            .First(i => ReferenceEquals(i.Source, vm.NewImage));
        var before = control.GetVisualDescendants().OfType<Image>()
            .First(i => ReferenceEquals(i.Source, vm.OldImage));

        var box = new Size(stage.Bounds.Width, stage.Bounds.Height);
        // The letterboxed rects for each revision inside the stage box (different aspect → different
        // rects). The after (tall) rect is inset horizontally; the before (wide) rect fills the width.
        var afterRect = ImageDiffControl.RenderedImageRect(after.Source!.Size, box);
        var beforeRect = ImageDiffControl.RenderedImageRect(before.Source!.Size, box);
        var boundaryX = afterRect.Left + 0.5 * afterRect.Width;

        Assert.True(afterRect.Left > 0.5, "expected the after image to be letterboxed (inset) in the box");
        Assert.True(afterRect.Width < stage.Bounds.Width - 1, "rendered rect must be narrower than the stage");
        // The wipe boundary sits on the *image* edge (inside the letterboxed rect), NOT at the control
        // midpoint offset by the raw width — i.e. it isn't 0.5*stageWidth.
        Assert.NotEqual(0.5 * stage.Bounds.Width, afterRect.Width, 1);

        // Each Image is sized to its own letterbox, so clips are in the image's LOCAL space.
        // (a) after revealed as its left slice [0, 0.5*afterRect.W] — width follows the rendered
        // image, not the control (0.5*afterRect.W, well under 0.5*stageWidth).
        var afterClip = Assert.IsType<RectangleGeometry>(after.Clip);
        AssertRectEqual(new Rect(0, 0, 0.5 * afterRect.Width, afterRect.Height), afterClip.Rect);
        Assert.True(afterClip.Rect.Width < 0.5 * stage.Bounds.Width - 1,
            "clip must follow the rendered image width, not the control width");

        // no-bleed: before clipped to the complementary right slice of the shared after rect,
        // translated into the before image's local space — so the old image stops at the divider.
        var beforeClip = Assert.IsType<RectangleGeometry>(before.Clip);
        AssertRectEqual(new Rect(boundaryX - beforeRect.Left, afterRect.Top - beforeRect.Top,
            afterRect.Right - boundaryX, afterRect.Height), beforeClip.Rect);

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "image_diff_wipe_unequal.png"));

        // (b) onion at 0.5 → even crossfade; before fully gone at 1.0.
        vm.IsOnionSkin = true;
        vm.SwipePosition = 0.5;
        Settle();
        Assert.Equal(0.5, after.Opacity, 3);
        Assert.Equal(0.5, before.Opacity, 3);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "image_diff_onion_unequal.png"));

        vm.SwipePosition = 1.0;
        Settle();
        Assert.Equal(0.0, before.Opacity, 3);
        HarnessHygiene.Teardown(win);
    }

    private static void AssertRectEqual(Rect expected, Rect actual)
    {
        Assert.Equal(expected.X, actual.X, 1);
        Assert.Equal(expected.Y, actual.Y, 1);
        Assert.Equal(expected.Width, actual.Width, 1);
        Assert.Equal(expected.Height, actual.Height, 1);
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

        // Pointer-X maps to a fraction of the rendered image rect (not the raw stage width), so the
        // test points are measured across that letterboxed rect. Press at ~20%, drag to ~75%.
        var rect = ImageDiffControl.RenderedImageRect(
            vm.NewImage!.Size, new Size(stage.Bounds.Width, stage.Bounds.Height));
        var y = stage.Bounds.Height / 2;
        var pStart = PointOnWindow(stage, win, rect.Left + 0.20 * rect.Width, y);
        var pEnd = PointOnWindow(stage, win, rect.Left + 0.75 * rect.Width, y);

        win.MouseDown(pStart, MouseButton.Left);
        Settle();
        Assert.InRange(vm.SwipePosition, 0.12, 0.28);

        win.MouseMove(pEnd);
        win.MouseUp(pEnd, MouseButton.Left);
        Settle();
        Assert.InRange(vm.SwipePosition, 0.67, 0.83);
        HarnessHygiene.Teardown(win);
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
