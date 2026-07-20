using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Mainguard.UI.Theming;

namespace Mainguard.App.Shell.Controls;

/// <summary>
/// A <see cref="TextBlock"/> that renders a single diff line with intra-line (word-level) emphasis
/// (T-13). It contains NO diff algorithm — the changed spans are precomputed by the pure Core
/// engine (<c>IntraLineDiff</c> / <c>WhitespaceMarkers</c>) and handed in via <see cref="SpansSource"/>
/// and <see cref="TrailingWhitespaceSpan"/>. This control only splits the text into styled runs.
///
/// Emphasis and whitespace-marker colors resolve from the active theme (no raw colors) and
/// re-resolve on <see cref="ThemeManager.ThemeChanged"/> while attached, per the AGENTS.md pattern.
/// </summary>
public sealed class IntraLineDiffTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceTextProperty =
        AvaloniaProperty.Register<IntraLineDiffTextBlock, string?>(nameof(SourceText));

    public static readonly StyledProperty<IReadOnlyList<(int Start, int Length)>?> SpansSourceProperty =
        AvaloniaProperty.Register<IntraLineDiffTextBlock, IReadOnlyList<(int Start, int Length)>?>(nameof(SpansSource));

    public static readonly StyledProperty<(int Start, int Length)?> TrailingWhitespaceSpanProperty =
        AvaloniaProperty.Register<IntraLineDiffTextBlock, (int Start, int Length)?>(nameof(TrailingWhitespaceSpan));

    public static readonly StyledProperty<string> EmphasisResourceKeyProperty =
        AvaloniaProperty.Register<IntraLineDiffTextBlock, string>(nameof(EmphasisResourceKey), "DiffAddedEmphasis");

    public static readonly StyledProperty<string> WhitespaceResourceKeyProperty =
        AvaloniaProperty.Register<IntraLineDiffTextBlock, string>(nameof(WhitespaceResourceKey), "DiffWhitespaceMarker");

    public string? SourceText
    {
        get => GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    public IReadOnlyList<(int Start, int Length)>? SpansSource
    {
        get => GetValue(SpansSourceProperty);
        set => SetValue(SpansSourceProperty, value);
    }

    public (int Start, int Length)? TrailingWhitespaceSpan
    {
        get => GetValue(TrailingWhitespaceSpanProperty);
        set => SetValue(TrailingWhitespaceSpanProperty, value);
    }

    public string EmphasisResourceKey
    {
        get => GetValue(EmphasisResourceKeyProperty);
        set => SetValue(EmphasisResourceKeyProperty, value);
    }

    public string WhitespaceResourceKey
    {
        get => GetValue(WhitespaceResourceKeyProperty);
        set => SetValue(WhitespaceResourceKeyProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ThemeManager.ThemeChanged += Rebuild;
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ThemeManager.ThemeChanged -= Rebuild;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceTextProperty
            || change.Property == SpansSourceProperty
            || change.Property == TrailingWhitespaceSpanProperty
            || change.Property == EmphasisResourceKeyProperty
            || change.Property == WhitespaceResourceKeyProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        var text = SourceText ?? string.Empty;
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();

        if (text.Length == 0) return;

        var emphasis = ResolveBrush(EmphasisResourceKey);
        var whitespace = ResolveBrush(WhitespaceResourceKey);

        // Per-character background: trailing whitespace first (lower precedence), emphasis overwrites.
        var bg = new IBrush?[text.Length];
        if (TrailingWhitespaceSpan is { } ws) Paint(bg, ws.Start, ws.Length, whitespace);
        var spans = SpansSource;
        if (spans != null)
            foreach (var (start, length) in spans) Paint(bg, start, length, emphasis);

        // Coalesce equal-background runs into as few Runs as possible.
        int i = 0;
        while (i < text.Length)
        {
            int runStart = i;
            var brush = bg[i];
            while (i < text.Length && ReferenceEquals(bg[i], brush)) i++;
            var run = new Run(text.Substring(runStart, i - runStart));
            if (brush != null) run.Background = brush;
            inlines.Add(run);
        }
    }

    private static void Paint(IBrush?[] bg, int start, int length, IBrush? brush)
    {
        if (brush == null || length <= 0) return;
        int from = System.Math.Clamp(start, 0, bg.Length);
        int to = System.Math.Clamp(start + length, 0, bg.Length);
        for (int i = from; i < to; i++) bg[i] = brush;
    }

    private IBrush? ResolveBrush(string key)
    {
        if (Application.Current is { } app
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
        {
            return brush;
        }
        return null;
    }
}
