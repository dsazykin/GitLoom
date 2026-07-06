using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// View state for the image-diff control (T-13): the decoded before/after bitmaps, their byte
/// sizes, and the swipe/onion-skin position. The DiffViewerViewModel populates it when a change is
/// a recognized image blob pair (detection lives in the pure <see cref="ImageDiffDetection"/>).
///
/// GROUNDWORK ONLY — the swipe interaction *feel* is deferred for human review; see
/// <see cref="SwipePosition"/> and the ImageDiffControl TODO marker.
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

    // 0..1 position for a future swipe/onion-skin control. 0.5 = both at half opacity.
    // TODO(T-13 human-review): image-diff swipe control feel — the interaction that drives this.
    [ObservableProperty]
    private double _swipePosition = 0.5;

    public string SizeSummary => ImageDiffDetection.FormatBinarySummary(OldSize, NewSize);

    public bool HasOldImage => OldImage != null;
    public bool HasNewImage => NewImage != null;

    partial void OnOldImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasOldImage));
    partial void OnNewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(HasNewImage));

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
