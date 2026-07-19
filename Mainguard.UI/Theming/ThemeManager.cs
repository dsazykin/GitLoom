using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

namespace GitLoom.App.Theming;

/// <summary>
/// Runtime theme switching. Each theme is a ResourceDictionary in Themes/ (now under
/// <c>Mainguard.UI</c>, step 2c) that defines the full token contract (see AGENTS.md, UI /
/// Design System). Views reference tokens with DynamicResource, so swapping the merged
/// dictionary restyles the app live. The chosen theme key is persisted through the
/// <see cref="PersistKey"/> seam the shell wires to its settings store — Mainguard.UI is the
/// base UI layer and must not reach up into <c>GitLoom.App.App.Settings</c> for persistence.
/// </summary>
public static class ThemeManager
{
    public sealed record ThemeInfo(string Key, string DisplayName, ThemeVariant Variant);

    public const string DefaultKey = "MidnightLoom";

    /// <summary>
    /// Persistence seam (step 2c): the shell sets this to write the chosen theme key into its own
    /// settings store (<c>App.Settings.Update(p =&gt; p.Theme = key)</c>). Left null it's a no-op —
    /// which is exactly what the headless render harnesses want (they always call
    /// <see cref="Apply"/> with <c>persist: false</c>). Keeps this design-system component from
    /// depending on the shell it sits beneath.
    /// </summary>
    public static Action<string>? PersistKey { get; set; }

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

        var uri = new Uri($"avares://Mainguard.UI/Themes/{theme.Key}.axaml");
        merged.Add(new ResourceInclude(uri) { Source = uri });
        app.RequestedThemeVariant = theme.Variant;

        CurrentKey = theme.Key;
        if (persist)
            PersistKey?.Invoke(theme.Key);

        ThemeChanged?.Invoke();
    }
}
