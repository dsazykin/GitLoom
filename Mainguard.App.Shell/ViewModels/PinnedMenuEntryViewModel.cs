using Avalonia.Media;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>One pinned top-nav icon button (#78) — Id matches PinnableMenus/UserPreferences.PinnedMenuIds.</summary>
public sealed class PinnedMenuEntryViewModel
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required Geometry IconResource { get; init; }
}
