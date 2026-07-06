using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Controls;

// Code-behind for the T-13 / T-13b image-diff control. State lives on ImageDiffViewModel; the
// overlay *geometry* lives here because it needs the measured stage bounds:
//   * Wipe mode      — the after image is clipped to [0, SwipePosition*width]; the before image
//                      underneath shows through on the right, and a divider line+handle sits at the
//                      boundary. Pressing/dragging anywhere on the stage maps pointer-X → SwipePosition.
//   * Onion-skin mode — the divider is hidden and the after image's opacity is SwipePosition, blended
//                      over the before image; the slider drives it.
// Both images fill the same box with Stretch=Uniform, so they stay pixel-aligned regardless of size.
public partial class ImageDiffControl : UserControl
{
    private Panel? _stage;
    private Image? _afterImage;
    private Panel? _dividerLayer;
    private TranslateTransform? _dividerTransform;
    private ImageDiffViewModel? _vm;
    private bool _dragging;

    public ImageDiffControl()
    {
        InitializeComponent();

        _stage = this.FindControl<Panel>("WipeStage");
        _afterImage = this.FindControl<Image>("AfterImage");
        _dividerLayer = this.FindControl<Panel>("DividerLayer");
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
    private void SetPositionFromPointer(Point p)
    {
        if (_stage == null) return;
        var w = _stage.Bounds.Width;
        if (w <= 0 || _vm == null) return;
        _vm.SwipePosition = Math.Clamp(p.X / w, 0, 1);
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

    // Applies the current mode + position to the overlay: clip/divider (Wipe) or opacity (Onion-skin).
    private void RefreshOverlay()
    {
        if (_stage == null || _afterImage == null || _dividerLayer == null || _vm == null) return;

        var w = _stage.Bounds.Width;
        var h = _stage.Bounds.Height;
        var pos = Math.Clamp(_vm.SwipePosition, 0, 1);

        if (_vm.IsOnionSkin)
        {
            _afterImage.Clip = null;
            _afterImage.Opacity = pos;
            _dividerLayer.IsVisible = false;
        }
        else
        {
            _afterImage.Opacity = 1;
            _afterImage.Clip = new RectangleGeometry(new Rect(0, 0, pos * w, h));
            _dividerLayer.IsVisible = true;
            if (_dividerTransform != null) _dividerTransform.X = pos * w;
        }
    }
}
