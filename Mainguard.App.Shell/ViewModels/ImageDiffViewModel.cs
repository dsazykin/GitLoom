using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// View state for the image-diff control (T-13): the decoded before/after bitmaps, their byte
/// sizes, and the swipe/onion-skin position. The DiffViewerViewModel populates it when a change is
/// a recognized image blob pair (detection lives in the pure <see cref="ImageDiffDetection"/>).
///
/// The reveal interaction (T-13b) lives in <see cref="Controls.ImageDiffControl"/>: <see cref="SwipePosition"/>
/// (0..1) drives either a vertical wipe divider (Wipe mode) or a before/after crossfade (Onion-skin
/// mode, toggled by <see cref="IsOnionSkin"/>).
/// </summary>
public partial class ImageDiffViewModel : ViewModelBase
{
    [ObservableProperty]
    private Bitmap? _oldImage;

    [ObservableProperty]
    private Bitmap? _newImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeSummary))]
    private long _oldSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeSummary))]
    private long _newSize;

    // 0..1 reveal position driven by the divider drag (Wipe) or the slider (Onion-skin).
    // Wipe mode: fraction of the rendered image width showing the *after* image (0 = all before,
    // 1 = all after).
    // Onion-skin mode: crossfade blend — after opacity = SwipePosition, before opacity =
    // 1-SwipePosition (0 = only before, 1 = only after, 0.5 = even blend).
    [ObservableProperty]
    private double _swipePosition = 0.5;

    // Reveal mode toggle: false = Wipe (drag a vertical divider), true = Onion-skin (crossfade).
    [ObservableProperty]
    private bool _isOnionSkin;

    public string SizeSummary => ImageDiffDetection.FormatBinarySummary(OldSize, NewSize);

    public bool HasOldImage => OldImage != null;
    public bool HasNewImage => NewImage != null;

    /// <summary>Both revisions decoded — the overlay/divider is only meaningful when true.</summary>
    public bool HasBothImages => HasOldImage && HasNewImage;

    partial void OnOldImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasOldImage));
        OnPropertyChanged(nameof(HasBothImages));
    }

    partial void OnNewImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasNewImage));
        OnPropertyChanged(nameof(HasBothImages));
    }

    /// <summary>Decodes and swaps in the before/after bitmaps, disposing any previous ones.</summary>
    public void SetImages(byte[]? oldBytes, byte[]? newBytes)
    {
        var oldDecoded = TryDecode(oldBytes);
        var newDecoded = TryDecode(newBytes);

        OldImage?.Dispose();
        NewImage?.Dispose();
        OldImage = oldDecoded;
        NewImage = newDecoded;
    }

    /// <summary>Releases the bitmaps and resets sizes — called when the viewer switches files.</summary>
    public void Clear()
    {
        OldImage?.Dispose();
        NewImage?.Dispose();
        OldImage = null;
        NewImage = null;
        OldSize = 0;
        NewSize = 0;
        SwipePosition = 0.5;
        IsOnionSkin = false;
    }

    private static Bitmap? TryDecode(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            using var ms = new System.IO.MemoryStream(bytes);
            return new Bitmap(ms);
        }
        catch
        {
            // Undecodable / unsupported image payload — the control shows a placeholder instead.
            return null;
        }
    }
}
