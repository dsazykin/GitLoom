using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Mainguard.Agents.Terminal;

namespace GitLoom.App.Controls;

/// <summary>Raised when the control's layout resolves a new terminal size (columns × rows).</summary>
internal sealed class TerminalResizeEventArgs : EventArgs
{
    public TerminalResizeEventArgs(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
    }

    public int Cols { get; }
    public int Rows { get; }
}

/// <summary>
/// The interim terminal renderer: a themed monospace cell-grid drawn from a pure
/// <see cref="VtScreen"/>, exposed to the ViewModel <b>only</b> through <see cref="ITerminalView"/>
/// so the P2-18 libvterm engine can replace it without any ViewModel change (invariant 3). No VT
/// parsing or terminal logic lives in the View/code-behind — it all lives in <see cref="VtScreen"/>
/// behind this control.
///
/// <para>Rendering is dirty-flag driven: the control invalidates only when new output arrives or a
/// resize happens, never on an idle timer, so there are no idle redraws. A test-only grid-readback
/// hook (<see cref="ReadGrid"/> / <see cref="FeedSync"/>) is exposed to <c>GitLoom.Tests</c> for the
/// P2-04 "feed bytes → read grid" harness.</para>
/// </summary>
public sealed class TerminalControl : Control, ITerminalView
{
    private const double FontSize = 14.0;
    private static readonly FontFamily MonoFamily =
        new("Cascadia Mono,Cascadia Code,Consolas,Menlo,DejaVu Sans Mono,monospace");

    private readonly Typeface _typeface = new(MonoFamily);
    private readonly Dictionary<uint, ImmutableSolidColorBrush> _brushCache = new();

    private VtScreen _screen = new(80, 24);
    private double _cellWidth;
    private double _cellHeight;

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;
        MeasureCell();
    }

    /// <inheritdoc />
    public event Action<byte[]>? InputAvailable;

    /// <summary>Raised when the control's own layout produces a new (cols, rows) size.</summary>
    internal event EventHandler<TerminalResizeEventArgs>? UserResized;

    /// <inheritdoc />
    public void FeedOutput(ReadOnlyMemory<byte> data)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Apply(data.Span);
        }
        else
        {
            var copy = data.ToArray();
            Dispatcher.UIThread.Post(() => Apply(copy));
        }
    }

    /// <inheritdoc />
    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0)
        {
            return;
        }

        _screen.Resize(cols, rows);
        InvalidateVisual();
    }

    /// <inheritdoc />
    public object GetStateSnapshot() => _screen.ReadGrid();

    /// <inheritdoc />
    public void RestoreState(object snapshot)
    {
        if (snapshot is TerminalGridSnapshot grid)
        {
            _screen.Restore(grid);
            InvalidateVisual();
        }
    }

    /// <summary>Test-only readback hook (P2-04): the current visible grid + cursor.</summary>
    internal TerminalGridSnapshot ReadGrid() => _screen.ReadGrid();

    /// <summary>Test-only synchronous feed (P2-04): parse bytes without the UI-thread marshal.</summary>
    internal void FeedSync(ReadOnlySpan<byte> data) => _screen.Feed(data);

    private void Apply(ReadOnlySpan<byte> data)
    {
        _screen.Feed(data);
        InvalidateVisual(); // dirty-flag: redraw only when bytes arrive
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Fill the available space; the renderer maps pixels → cells.
        var w = double.IsInfinity(availableSize.Width) ? _cellWidth * 80 : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? _cellHeight * 24 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateGridSizeFromBounds(finalSize);
        return result;
    }

    private void UpdateGridSizeFromBounds(Size size)
    {
        if (_cellWidth <= 0 || _cellHeight <= 0)
        {
            return;
        }

        var cols = Math.Max(1, (int)(size.Width / _cellWidth));
        var rows = Math.Max(1, (int)(size.Height / _cellHeight));
        if (cols == _screen.Cols && rows == _screen.Rows)
        {
            return;
        }

        UserResized?.Invoke(this, new TerminalResizeEventArgs(cols, rows));
    }

    public override void Render(DrawingContext context)
    {
        var background = ResolveBrush("TerminalBackground", 0xFF0B0D10);
        var foreground = ResolveBrush("TerminalForeground", 0xFFE6E9EF);
        context.FillRectangle(background, new Rect(Bounds.Size));

        var rows = _screen.VisibleRows;
        for (var r = 0; r < _screen.Rows; r++)
        {
            RenderRow(context, rows[r], r, foreground, background);
        }

        RenderCursor(context);
    }

    private void RenderRow(DrawingContext context, TerminalCell[] row, int rowIndex, IBrush defaultFg, IBrush defaultBg)
    {
        var y = rowIndex * _cellHeight;

        // 1) Backgrounds: fill runs of non-default background.
        for (var c = 0; c < row.Length;)
        {
            var bg = row[c].Bg;
            if (bg < 0)
            {
                c++;
                continue;
            }

            var start = c;
            while (c < row.Length && row[c].Bg == bg)
            {
                c++;
            }

            var brush = BrushForAnsi(bg, defaultBg);
            context.FillRectangle(brush, new Rect(start * _cellWidth, y, (c - start) * _cellWidth, _cellHeight));
        }

        // 2) Glyph runs: group by (fg, bold), skip blanks.
        var sb = new StringBuilder();
        var runStart = 0;
        var runFg = int.MinValue;
        var runBold = false;

        void Flush(int endExclusive)
        {
            if (sb.Length == 0)
            {
                return;
            }

            var brush = BrushForAnsi(runFg, defaultFg);
            var typeface = runBold ? new Typeface(MonoFamily, FontStyle.Normal, FontWeight.Bold) : _typeface;
            var text = new FormattedText(
                sb.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, FontSize, brush);
            context.DrawText(text, new Point(runStart * _cellWidth, y));
            sb.Clear();
        }

        for (var c = 0; c < row.Length; c++)
        {
            var cell = row[c];
            var glyph = string.IsNullOrEmpty(cell.Glyph) ? " " : cell.Glyph;
            if (glyph == " ")
            {
                Flush(c);
                runStart = c + 1;
                continue;
            }

            if (sb.Length == 0)
            {
                runStart = c;
                runFg = cell.Fg;
                runBold = cell.Bold;
            }
            else if (cell.Fg != runFg || cell.Bold != runBold)
            {
                Flush(c);
                runStart = c;
                runFg = cell.Fg;
                runBold = cell.Bold;
            }

            sb.Append(glyph);
        }

        Flush(row.Length);
    }

    private void RenderCursor(DrawingContext context)
    {
        if (_screen.Rows == 0 || _screen.Cols == 0)
        {
            return;
        }

        var cursor = ResolveBrush("TerminalCursor", 0xFF8B8BF5);
        var rect = new Rect(_screen.CursorCol * _cellWidth, _screen.CursorRow * _cellHeight, _cellWidth, _cellHeight);
        context.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)cursor).Color, 0.5), rect);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var bytes = MapKey(e.Key, e.KeyModifiers);
        if (bytes is not null)
        {
            InputAvailable?.Invoke(bytes);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text))
        {
            InputAvailable?.Invoke(Encoding.UTF8.GetBytes(e.Text));
            e.Handled = true;
            return;
        }

        base.OnTextInput(e);
    }

    /// <summary>Maps a keystroke to the bytes sent toward the PTY. Internal for direct testing.</summary>
    internal static byte[]? MapKey(Key key, KeyModifiers modifiers)
    {
        var ctrl = modifiers.HasFlag(KeyModifiers.Control);

        // Ctrl+<letter> → the corresponding C0 control byte (Ctrl+C = 0x03, Ctrl+D = 0x04, …).
        if (ctrl && key >= Key.A && key <= Key.Z)
        {
            return new[] { (byte)(key - Key.A + 1) };
        }

        return key switch
        {
            Key.Enter => new byte[] { 0x0D },
            Key.Back => new byte[] { 0x7F },
            Key.Tab => new byte[] { 0x09 },
            Key.Escape => new byte[] { 0x1B },
            Key.Up => Esc("[A"),
            Key.Down => Esc("[B"),
            Key.Right => Esc("[C"),
            Key.Left => Esc("[D"),
            Key.Home => Esc("[H"),
            Key.End => Esc("[F"),
            Key.PageUp => Esc("[5~"),
            Key.PageDown => Esc("[6~"),
            Key.Delete => Esc("[3~"),
            Key.Insert => Esc("[2~"),
            Key.F1 => Esc("OP"),
            Key.F2 => Esc("OQ"),
            Key.F3 => Esc("OR"),
            Key.F4 => Esc("OS"),
            Key.F5 => Esc("[15~"),
            Key.F6 => Esc("[17~"),
            Key.F7 => Esc("[18~"),
            Key.F8 => Esc("[19~"),
            Key.F9 => Esc("[20~"),
            Key.F10 => Esc("[21~"),
            Key.F11 => Esc("[23~"),
            Key.F12 => Esc("[24~"),
            _ => null,
        };
    }

    private static byte[] Esc(string tail)
    {
        var bytes = new byte[tail.Length + 1];
        bytes[0] = 0x1B;
        Encoding.ASCII.GetBytes(tail, 0, tail.Length, bytes, 1);
        return bytes;
    }

    private void MeasureCell()
    {
        var probe = new FormattedText(
            "M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.White);
        _cellWidth = probe.WidthIncludingTrailingWhitespace > 0 ? probe.WidthIncludingTrailingWhitespace : FontSize * 0.6;
        _cellHeight = probe.Height > 0 ? probe.Height : FontSize * 1.3;
    }

    private IBrush ResolveBrush(string key, uint fallbackArgb)
    {
        if (this.TryFindResource(key, out var value) && value is ISolidColorBrush scb)
        {
            return scb;
        }

        return BrushFor(fallbackArgb);
    }

    private IBrush BrushForAnsi(int index, IBrush fallback)
    {
        if (index < 0)
        {
            return fallback;
        }

        if (index < 16)
        {
            return ResolveBrush("TerminalAnsi" + index, XtermArgb(index));
        }

        return BrushFor(XtermArgb(index));
    }

    private ImmutableSolidColorBrush BrushFor(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out var brush))
        {
            return brush;
        }

        brush = new ImmutableSolidColorBrush(Color.FromUInt32(argb));
        _brushCache[argb] = brush;
        return brush;
    }

    /// <summary>Standard xterm 256-colour value for an index (used for 16–255, and as a token fallback).</summary>
    private static uint XtermArgb(int index)
    {
        // 0–15: base ANSI (approximate; the theme tokens override these for 0–15 at render time).
        ReadOnlySpan<uint> baseColors = new uint[]
        {
            0xFF000000, 0xFFCD3131, 0xFF2AA745, 0xFFC7C43B, 0xFF3B6FD6, 0xFFB454C9, 0xFF2EC5CE, 0xFFD0D0D0,
            0xFF6A737D, 0xFFF14C4C, 0xFF3FC56B, 0xFFEAE05B, 0xFF5C8DF0, 0xFFD67BEA, 0xFF4FE0E8, 0xFFFFFFFF,
        };

        if (index < 16)
        {
            return baseColors[index];
        }

        if (index < 232)
        {
            var i = index - 16;
            var r = i / 36;
            var g = i / 6 % 6;
            var b = i % 6;
            return Rgb(Step(r), Step(g), Step(b));
        }

        var gray = (byte)(8 + (index - 232) * 10);
        return Rgb(gray, gray, gray);
    }

    private static byte Step(int cubeComponent) => cubeComponent == 0 ? (byte)0 : (byte)(55 + cubeComponent * 40);

    private static uint Rgb(byte r, byte g, byte b)
        => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
}
