using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace GitLoom.App.Theming;

/// <summary>
/// Runtime theme switching. Each theme is a ResourceDictionary in Themes/ that
/// defines the full token contract (see AGENTS.md, UI / Design System). Views
/// reference tokens with DynamicResource, so swapping the merged dictionary
/// restyles the app live. The chosen theme key is persisted in
/// UserPreferences.Theme via App.Settings.
/// </summary>
public static class ThemeManager
{
    public sealed record ThemeInfo(string Key, string DisplayName, ThemeVariant Variant);

    public const string DefaultKey = "MidnightLoom";

    public static readonly IReadOnlyList<ThemeInfo> Themes = new[]
    {
        new ThemeInfo("MidnightLoom", "Midnight Loom", ThemeVariant.Dark),
        new ThemeInfo("DaylightLoom", "Daylight Loom", ThemeVariant.Light),
        new ThemeInfo("CommandDeck", "Command Deck", ThemeVariant.Dark),
        new ThemeInfo("Atelier", "Atelier", ThemeVariant.Dark),
        new ThemeInfo("LoomAurora", "Loom Aurora", ThemeVariant.Dark),
    };

    public static string CurrentKey { get; private set; } = DefaultKey;

    /// <summary>Raised after a theme is applied. Code-drawn consumers
    /// (e.g. CommitGraphCanvas) re-resolve their brushes on this.</summary>
    public static event Action? ThemeChanged;

    /// <summary>Apply the persisted (or default) theme at startup, before the main window opens.</summary>
    public static void Initialize(string? savedKey)
    {
        var key = Themes.Any(t => t.Key == savedKey) ? savedKey! : DefaultKey;
        Apply(key, persist: false);
    }

    public static void Apply(string key, bool persist = true)
    {
        var app = Application.Current;
        if (app is null) return;

        var theme = Themes.FirstOrDefault(t => t.Key == key) ?? Themes[0];

        var merged = app.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            if (merged[i] is ResourceInclude ri && ri.Source?.ToString().Contains("/Themes/") == true)
                merged.RemoveAt(i);
        }

        var uri = new Uri($"avares://GitLoom.App/Themes/{theme.Key}.axaml");
        merged.Add(new ResourceInclude(uri) { Source = uri });
        app.RequestedThemeVariant = theme.Variant;

        CurrentKey = theme.Key;
        if (persist)
            App.Settings.Update(p => p.Theme = theme.Key);

        ThemeChanged?.Invoke();
    }
}
