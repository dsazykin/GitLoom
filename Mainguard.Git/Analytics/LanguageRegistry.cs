using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Mainguard.Git.Analytics;

public static class LanguageRegistry
{
    private static readonly Dictionary<string, LanguageModel> _extensionToLanguageMap = new(StringComparer.OrdinalIgnoreCase);

    static LanguageRegistry()
    {
        InitializeRegistry();
    }

    private static void InitializeRegistry()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Mainguard.Git.Analytics.languages.json";

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return;

            using StreamReader reader = new StreamReader(stream);
            string json = reader.ReadToEnd();

            var languages = JsonSerializer.Deserialize<List<LanguageModel>>(json);
            if (languages != null)
            {
                foreach (var lang in languages)
                {
                    foreach (var ext in lang.Extensions)
                    {
                        _extensionToLanguageMap[ext] = lang;
                    }
                }
            }
        }
        catch
        {
            // Fallback gracefully on initialization error
        }
    }

    public static LanguageModel? GetLanguageByExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;

        _extensionToLanguageMap.TryGetValue(extension, out var language);
        return language;
    }
}
