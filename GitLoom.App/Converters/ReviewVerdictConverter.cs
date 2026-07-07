using System;
using System.Globalization;
using Avalonia.Data.Converters;
using GitLoom.Core.Models;

namespace GitLoom.App.Converters;

public class ReviewVerdictConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is ReviewVerdict verdict)
        {
            return verdict switch
            {
                ReviewVerdict.Comment => "Comment",
                ReviewVerdict.Approve => "Approve",
                ReviewVerdict.RequestChanges => "Request Changes",
                _ => verdict.ToString()
            };
        }
        return value?.ToString() ?? string.Empty;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
