using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using GitLoom.App.ViewModels.Agents;

namespace GitLoom.App.Converters;

/// <summary>
/// The ONE <see cref="AgentStatus"/> → brush mapping in the app (P2-13 invariant #2). Resolves a
/// design token by key from the active theme — never a literal brush, never a hard-coded color.
/// The token set exists in every <c>Themes/*.axaml</c>; a missing key is a build-your-theme bug,
/// caught by <c>AgentStatusBrushTests.StatusBrush_MappingComplete</c>. Because it resolves against
/// <see cref="Application.ActualThemeVariant"/>, a live theme switch is followed by re-raising the
/// bound property (see <c>AgentRowViewModel</c>) so badges re-tint without a rebuild.
/// </summary>
public sealed class AgentStatusBrushConverter : IValueConverter
{
    public static readonly AgentStatusBrushConverter Instance = new();

    /// <summary>The theme-resource key for a status. Pure and total — the single source of the
    /// status→token contract the theme-completeness test enumerates.</summary>
    public static string TokenKeyFor(AgentStatus status) => status switch
    {
        AgentStatus.Working => "AgentStatusWorkingBrush",
        AgentStatus.Verifying => "AgentStatusVerifyingBrush",
        AgentStatus.Verified => "AgentStatusVerifiedBrush",
        AgentStatus.Stale => "AgentStatusStaleBrush",
        AgentStatus.AwaitingReview => "AgentStatusAwaitingReviewBrush",
        AgentStatus.Conflict => "AgentStatusConflictBrush",
        AgentStatus.RateLimited => "AgentStatusRateLimitedBrush",
        AgentStatus.Dead => "AgentStatusDeadBrush",
        AgentStatus.Paused => "AgentStatusPausedBrush",
        _ => "TextMuted",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AgentStatus status) return AvaloniaProperty.UnsetValue;
        var app = Application.Current;
        if (app is null) return AvaloniaProperty.UnsetValue;
        return app.TryGetResource(TokenKeyFor(status), app.ActualThemeVariant, out var res) && res is IBrush brush
            ? brush
            : AvaloniaProperty.UnsetValue;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
