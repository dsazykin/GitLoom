using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Controls;

// Code-behind for the T-13 / T-13b image-diff control. State lives on ImageDiffViewModel; the
// overlay *geometry* lives here because it needs the measured stage bounds. Both images fill the
// same box with Stretch=Uniform, so they letterbox to the same centered *rendered rect* — computed
// by RenderedImageRect — and everything below is positioned against that rect, not the raw stage
// width (which is why an unequal-size before/after still lines up on the image edge):
//   * Wipe mode      — the after image is clipped to the left slice [rect.Left, rect.Left+pos*rect.W]
//                      and the before image to the complementary right slice, so the two meet exactly
//                      at the divider and the old image never bleeds past it. A divider line+handle
//                      sits at the boundary. Pressing/dragging the stage maps pointer-X (relative to
//                      the rendered rect) → SwipePosition.
//   * Onion-skin mode — the divider is hidden and the two images *crossfade*: before opacity is
//                      1-SwipePosition while after opacity is SwipePosition (0 → only before,
//                      1 → only after, 0.5 → even blend). The slider drives it.
public partial class ImageDiffControl : UserControl
{
    private Panel? _stage;
    private Image? _beforeImage;
    private Image? _afterImage;
    private Panel? _dividerLayer;
    private Rectangle? _dividerLine;
    private TranslateTransform? _dividerTransform;
    private ImageDiffViewModel? _vm;
    private bool _dragging;

    public ImageDiffControl()
    {
        InitializeComponent();

        _stage = this.FindControl<Panel>("WipeStage");
        _beforeImage = this.FindControl<Image>("BeforeImage");
        _afterImage = this.FindControl<Image>("AfterImage");
        _dividerLayer = this.FindControl<Panel>("DividerLayer");
        _dividerLine = this.FindControl<Rectangle>("DividerLine");
        _dividerTransform = _dividerLayer?.RenderTransform as TranslateTransform;

        if (_stage != null)
        {
            _stage.PointerPressed += OnStagePointerPressed;
            _stage.PointerMoved += OnStagePointerMoved;
            _stage.PointerReleased += OnStagePointerReleased;
            // Re-clip whenever the stage is (re)measured — a resize must keep the divider aligned.
            _stage.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty) RefreshOverlay();
            };
        }

        DataContextChanged += OnDataContextChanged;
        OnDataContextChanged(this, EventArgs.Empty);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as ImageDiffViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmPropertyChanged;
        RefreshOverlay();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageDiffViewModel.SwipePosition)
            or nameof(ImageDiffViewModel.IsOnionSkin)
            or nameof(ImageDiffViewModel.OldImage)
            or nameof(ImageDiffViewModel.NewImage)
            or nameof(ImageDiffViewModel.HasBothImages))
        {
            RefreshOverlay();
        }
    }

    // Maps a pointer position over the stage to a clamped 0..1 reveal fraction and writes it back.
    // The fraction is measured across the rendered image rect (letterboxed), not the raw stage
    // width — so the divider tracks the pointer against the visible image, and the draggable range
    // is clamped to the image edges.
    private void SetPositionFromPointer(Point p)
    {
        if (_stage == null || _vm == null) return;
        var rect = RenderedRect();
        if (rect.Width <= 0) return;
        _vm.SwipePosition = Math.Clamp((p.X - rect.Left) / rect.Width, 0, 1);
    }

    private Size StageBox()
        => _stage == null ? default : new Size(_stage.Bounds.Width, _stage.Bounds.Height);

    // The centered, letterboxed rectangle the after image renders into within the stage box under
    // Stretch=Uniform (panel coordinate space). Governs the divider position + the pointer mapping.
    private Rect RenderedRect()
        => _vm?.NewImage == null ? default : RenderedImageRect(_vm.NewImage.Size, StageBox());

    // Pure letterbox math for Stretch=Uniform: scale to the tighter axis, then center the result.
    // Exposed for the render harness to assert the wipe boundary lands on the image edge.
    public static Rect RenderedImageRect(Size image, Size box)
    {
        if (image.Width <= 0 || image.Height <= 0 || box.Width <= 0 || box.Height <= 0)
            return default;
        var scale = Math.Min(box.Width / image.Width, box.Height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        return new Rect((box.Width - w) / 2, (box.Height - h) / 2, w, h);
    }

    private void OnStagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm == null || _vm.IsOnionSkin || _stage == null) return;   // onion-skin uses the slider
        _dragging = true;
        e.Pointer.Capture(_stage);
        SetPositionFromPointer(e.GetPosition(_stage));
        e.Handled = true;
    }

    private void OnStagePointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || _stage == null) return;
        SetPositionFromPointer(e.GetPosition(_stage));
    }

    private void OnStagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }

    // Applies the current mode + position to the overlay: a rendered-rect clip/divider (Wipe) or a
    // crossfade (Onion-skin).
    private void RefreshOverlay()
    {
        if (_stage == null || _beforeImage == null || _afterImage == null
            || _dividerLayer == null || _vm == null) return;

        var pos = Math.Clamp(_vm.SwipePosition, 0, 1);

        if (_vm.IsOnionSkin)
        {
            // True crossfade: the before fades out as the after fades in, so the old image never
            // sits fully opaque underneath.
            _afterImage.Clip = null;
            _beforeImage.Clip = null;
            _beforeImage.Opacity = 1 - pos;
            _afterImage.Opacity = pos;
            _dividerLayer.IsVisible = false;
            return;
        }

        _beforeImage.Opacity = 1;
        _afterImage.Opacity = 1;

        var box = StageBox();
        var afterRect = _vm.NewImage != null ? RenderedImageRect(_vm.NewImage.Size, box) : default;
        var beforeRect = _vm.OldImage != null ? RenderedImageRect(_vm.OldImage.Size, box) : default;
        if (afterRect.Width <= 0 || afterRect.Height <= 0)
        {
            _afterImage.Clip = null;
            _beforeImage.Clip = null;
            _dividerLayer.IsVisible = false;
            return;
        }

        // The divider lives in the stage (panel) coordinate space; the boundary is anchored to the
        // *after* image's letterboxed rect so it lands on the image edge, not the control edge.
        var boundaryX = afterRect.Left + pos * afterRect.Width;

        // Each Image sizes itself to its own letterboxed content, so a Clip on it is in that image's
        // LOCAL space (origin at the content's top-left). Reveal the after's left slice, and clip the
        // before to the complementary right slice of the *shared* after rect — translated into the
        // before image's local space — so the old image can never bleed past the divider.
        _afterImage.Clip = new RectangleGeometry(
            new Rect(0, 0, pos * afterRect.Width, afterRect.Height));
        _beforeImage.Clip = new RectangleGeometry(
            new Rect(boundaryX - beforeRect.Left, afterRect.Top - beforeRect.Top,
                     afterRect.Right - boundaryX, afterRect.Height));

        _dividerLayer.IsVisible = true;
        if (_dividerLine != null) _dividerLine.Height = afterRect.Height;
        if (_dividerTransform != null) _dividerTransform.X = boundaryX;
    }
}
