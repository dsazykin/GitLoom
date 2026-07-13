using System;
using System.Globalization;
using Avalonia.Data.Converters;
using GitLoom.Core.Models;

namespace GitLoom.App.Converters;

/// <summary>
/// Maps a <see cref="DiffLineKind"/> to a boolean for a single style-class toggle, so the review cockpit
/// can tint add/delete lines with the shipped diff tokens without any rule logic in XAML. Use the static
/// <see cref="Add"/> / <see cref="Delete"/> instances (one per class binding).
/// </summary>
public sealed class DiffLineKindToClassConverter : IValueConverter
{
    public static readonly DiffLineKindToClassConverter Add = new(DiffLineKind.Add);
    public static readonly DiffLineKindToClassConverter Delete = new(DiffLineKind.Delete);

    private readonly DiffLineKind _match;

    private DiffLineKindToClassConverter(DiffLineKind match) => _match = match;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DiffLineKind kind && kind == _match;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
