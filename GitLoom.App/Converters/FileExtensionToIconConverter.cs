using System;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;

namespace GitLoom.App.Converters;

public class FileExtensionToIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string fileName)
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (fileName.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)) return "🙈";

            return ext switch
            {
                ".cs" => "c#",
                ".js" or ".jsx" => "js",
                ".ts" or ".tsx" => "ts",
                ".html" or ".htm" => "🌐",
                ".css" or ".scss" or ".less" => "🎨",
                ".json" => "{}",
                ".xml" or ".xaml" or ".axaml" => "🧩",
                ".md" => "📝",
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".ico" => "🖼️",
                ".txt" => "📄",
                ".sql" => "🗄️",
                ".sh" or ".bat" or ".cmd" or ".ps1" => "⚙️",
                ".csproj" or ".sln" => "📦",
                _ => "📄"
            };
        }
        return "📄";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
