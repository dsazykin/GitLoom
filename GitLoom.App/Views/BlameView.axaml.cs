using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;

namespace GitLoom.App.Views;

public partial class BlameView : UserControl
{
    private readonly BlameGutterMargin _gutter = new();

    public BlameView()
    {
        InitializeComponent();

        // The gutter is a left margin of the editor so it stays pixel-aligned with the text lines.
        Editor.TextArea.LeftMargins.Insert(0, _gutter);
        _gutter.CommitClicked += sha =>
        {
            if (DataContext is BlameViewModel vm) vm.SelectCommit(sha);
        };

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not BlameViewModel vm) return;

            SyncFromViewModel(vm);
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is nameof(BlameViewModel.RawContent)
                    or nameof(BlameViewModel.BlameLines))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SyncFromViewModel(vm));
                }
            };
        };
    }

    private void SyncFromViewModel(BlameViewModel vm)
    {
        if (Editor.Document == null)
        {
            Editor.Document = new AvaloniaEdit.Document.TextDocument();
        }
        if (Editor.Document.Text != (vm.RawContent ?? string.Empty))
        {
            Editor.Document.Text = vm.RawContent ?? string.Empty;
        }
        _gutter.SetLines(vm.BlameLines);
    }
}

/// <summary>
/// AvaloniaEdit left margin that renders per-line blame: an age-heat bar (recent → BlameAgeNew,
/// old → BlameAgeOld) plus <c>author · shortSha · relative-date</c>, with alternating dim shade on
/// commit boundaries; click selects that commit. Age-heat colors resolve from theme tokens at
/// render time so the gutter honors the active theme.
///
/// TODO(T-11 human-review): blame-gutter visual polish. The gutter is functionally wired (heat bar,
/// author/sha/date text, boundary shading, click-to-select, tooltip) but the exact metrics —
/// column width, font size, heat ramp/contrast across all five themes, tooltip styling, and live
/// redraw on a theme switch while blame is open — are deliberately left for tomorrow's visual pass.
/// </summary>
public sealed class BlameGutterMargin : AbstractMargin
{
    private const double GutterWidth = 232;
    private const double HeatBarWidth = 4;

    private IReadOnlyList<BlameLine> _lines = Array.Empty<BlameLine>();

    // Precomputed once per SetLines to keep Render allocation-free per frame.
    private long _oldestTicks;
    private long _newestTicks;
    private readonly Dictionary<string, bool> _shaDim = new();   // alternating shade per distinct commit

    /// <summary>Raised with the full SHA when a gutter row is clicked.</summary>
    public event Action<string>? CommitClicked;

    public void SetLines(IReadOnlyList<BlameLine> lines)
    {
        _lines = lines ?? Array.Empty<BlameLine>();

        _shaDim.Clear();
        _oldestTicks = long.MaxValue;
        _newestTicks = long.MinValue;
        int distinct = 0;
        foreach (var line in _lines)
        {
            var ticks = line.When.UtcTicks;
            if (ticks < _oldestTicks) _oldestTicks = ticks;
            if (ticks > _newestTicks) _newestTicks = ticks;
            if (!_shaDim.ContainsKey(line.Sha))
            {
                _shaDim[line.Sha] = (distinct % 2) == 1;
                distinct++;
            }
        }

        // Re-measure, not just re-render: the gutter's width flips 0 ↔ GutterWidth as lines arrive
        // or clear (blame loads async after the first, empty, sync), and InvalidateVisual alone would
        // not pick up the new MeasureOverride width — the gutter would stay collapsed at 0.
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(_lines.Count == 0 ? 0 : GutterWidth, 0);

    protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
    {
        if (oldTextView != null) oldTextView.VisualLinesChanged -= OnRedrawRequested;
        if (newTextView != null) newTextView.VisualLinesChanged += OnRedrawRequested;
        base.OnTextViewChanged(oldTextView, newTextView);
        InvalidateVisual();
    }

    private void OnRedrawRequested(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext drawingContext)
    {
        var textView = TextView;
        if (textView is not { VisualLinesValid: true } || _lines.Count == 0) return;

        var width = Bounds.Width;
        var heatNew = ResolveColor("BlameAgeNew", Color.FromRgb(0xE3, 0xB3, 0x41));
        var heatOld = ResolveColor("BlameAgeOld", Color.FromRgb(0x3B, 0x42, 0x52));
        var textBrush = ResolveBrush("TextMuted", Color.FromRgb(0x8A, 0x93, 0xA6));
        var dimShade = ResolveBrush("SurfaceHover", Color.FromRgb(0x25, 0x2B, 0x34));
        var typeface = new Typeface(ResolveMonoFamily());

        foreach (var visualLine in textView.VisualLines)
        {
            var docLine = visualLine.FirstDocumentLine;
            if (docLine == null || docLine.IsDeleted) continue;

            var blame = BlameForLine(docLine.LineNumber);
            if (blame == null) continue;

            var y = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop)
                    - textView.VerticalOffset;
            var height = visualLine.Height;

            // Alternating dim shade so consecutive lines of the same commit read as one block.
            if (_shaDim.TryGetValue(blame.Sha, out var dim) && dim)
            {
                drawingContext.FillRectangle(dimShade, new Rect(0, y, width, height), 0);
            }

            // Age-heat bar on the left edge.
            var heat = new SolidColorBrush(LerpHeat(blame, heatOld, heatNew));
            drawingContext.FillRectangle(heat, new Rect(0, y, HeatBarWidth, height));

            // author · shortSha · relative date
            var label = $"{Truncate(blame.AuthorName, 16)} · {blame.ShortSha} · {RelativeDate(blame.When)}";
            var formatted = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 11, textBrush);
            drawingContext.DrawText(formatted, new Point(HeatBarWidth + 6, y + (height - formatted.Height) / 2));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var blame = BlameAt(e.GetPosition(this).Y);
        if (blame != null) CommitClicked?.Invoke(blame.Sha);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var blame = BlameAt(e.GetPosition(this).Y);
        ToolTip.SetTip(this, blame == null ? null : $"{blame.Sha}\n{blame.Summary}");
    }

    private BlameLine? BlameAt(double viewY)
    {
        var textView = TextView;
        if (textView is not { VisualLinesValid: true }) return null;
        foreach (var visualLine in textView.VisualLines)
        {
            var top = visualLine.GetTextLineVisualYPosition(visualLine.TextLines[0], VisualYPosition.TextTop)
                      - textView.VerticalOffset;
            if (viewY >= top && viewY < top + visualLine.Height)
            {
                var docLine = visualLine.FirstDocumentLine;
                return docLine == null ? null : BlameForLine(docLine.LineNumber);
            }
        }
        return null;
    }

    // Blame rows are 1-based and contiguous over the current file, so index by line number.
    private BlameLine? BlameForLine(int lineNumber)
    {
        var idx = lineNumber - 1;
        if (idx < 0 || idx >= _lines.Count) return null;
        var line = _lines[idx];
        return line.LineNumber == lineNumber ? line : null;
    }

    private Color LerpHeat(BlameLine blame, Color old, Color recent)
    {
        if (_newestTicks <= _oldestTicks) return recent;
        var t = (double)(blame.When.UtcTicks - _oldestTicks) / (_newestTicks - _oldestTicks);
        t = Math.Clamp(t, 0, 1);
        return Lerp(old, recent, t);
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * t),
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max - 1) + "…");

    private static string RelativeDate(DateTimeOffset when)
    {
        var delta = DateTimeOffset.Now - when;
        if (delta.TotalDays < 0) return "now";
        if (delta.TotalDays >= 365) return $"{(int)(delta.TotalDays / 365)}y";
        if (delta.TotalDays >= 30) return $"{(int)(delta.TotalDays / 30)}mo";
        if (delta.TotalDays >= 1) return $"{(int)delta.TotalDays}d";
        if (delta.TotalHours >= 1) return $"{(int)delta.TotalHours}h";
        if (delta.TotalMinutes >= 1) return $"{(int)delta.TotalMinutes}m";
        return "now";
    }

    private static Color ResolveColor(string key, Color fallback)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is ISolidColorBrush b)
        {
            return b.Color;
        }
        return fallback;
    }

    private static IBrush ResolveBrush(string key, Color fallback)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush b)
        {
            return b;
        }
        return new SolidColorBrush(fallback);
    }

    private static FontFamily ResolveMonoFamily()
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource("FontMono", app.ActualThemeVariant, out var res) && res is FontFamily f)
        {
            return f;
        }
        return FontFamily.Parse("Consolas, Menlo, monospace");
    }
}
