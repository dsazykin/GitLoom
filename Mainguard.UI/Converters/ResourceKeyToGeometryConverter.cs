using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Mainguard.UI.Converters;

/// <summary>
/// Resolves an icon resource key (a theme-independent StreamGeometry in App.axaml) so a
/// ViewModel can name a badge form without holding an Avalonia type (ControlCenterDesign.md §9.3:
/// the AgentStatus → Geometry mapping rides the same converter pattern as status → brush).
/// </summary>
public class ResourceKeyToGeometryConverter : IValueConverter
{
    public static readonly ResourceKeyToGeometryConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string key && Application.Current is { } app && app.TryGetResource(key, null, out var resource)
            ? resource
            : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
