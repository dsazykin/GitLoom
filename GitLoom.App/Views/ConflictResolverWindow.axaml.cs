using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Rendering;
using GitLoom.Core.Models;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

// Synchronized 3-pane merge editor (T-04): Ours (read-only) | Result (editable) | Theirs (read-only).
// Each chunk is padded with filler lines so a conflict occupies the same vertical band in all three
// panes; scrolling is kept in lock-step. MergeBandRenderer tints regions; the accept/reject controls
// live in MergeGutter columns between the panes. Resolution logic lives in the ViewModel/engine —
// this code-behind only renders chunk state and turns gutter clicks / result edits into calls.
public partial class ConflictResolverWindow : Window
{
    private ConflictResolverWindowViewModel? _vm;
    private readonly List<MergeBand> _bands = new();
    private readonly HashSet<int> _resultFillerLines = new();

    private readonly MergeBandRenderer _oursRenderer;
    private readonly MergeBandRenderer _resultRenderer;
    private readonly MergeBandRenderer _theirsRenderer;

    private ScrollViewer? _oursSv, _resultSv, _theirsSv;
    private bool _scrollWired;
    private bool _syncingScroll;
    private bool _settingText;
    private bool _suppressRebuild;
    private bool _chunkHandlersAttached;
    private int _navIndex = -1;

    public ConflictResolverWindow()
    {
        InitializeComponent();

        _oursRenderer = new MergeBandRenderer(MergePane.Ours, _bands);
        _resultRenderer = new MergeBandRenderer(MergePane.Result, _bands);
        _theirsRenderer = new MergeBandRenderer(MergePane.Theirs, _bands);
        OursEditor.TextArea.TextView.BackgroundRenderers.Add(_oursRenderer);
        ResultEditor.TextArea.TextView.BackgroundRenderers.Add(_resultRenderer);
        TheirsEditor.TextArea.TextView.BackgroundRenderers.Add(_theirsRenderer);

        OursGutter.Configure(OursEditor, _bands, MergePane.Ours, OnGutterAction);
        TheirsGutter.Configure(TheirsEditor, _bands, MergePane.Theirs, OnGutterAction);

        // Redraw gutters whenever their side pane re-lays-out its lines (scroll, resize, doc change).
        OursEditor.TextArea.TextView.VisualLinesChanged += (_, __) => OursGutter.InvalidateVisual();
        TheirsEditor.TextArea.TextView.VisualLinesChanged += (_, __) => TheirsGutter.InvalidateVisual();

        ResultEditor.TextChanged += OnResultTextChanged;

        PrevConflictButton.Click += (_, __) => NavigateConflict(-1);
        NextConflictButton.Click += (_, __) => NavigateConflict(1);
        AcceptAllOursButton.Click += (_, __) => AcceptAll(ConflictSide.Ours);
        AcceptAllTheirsButton.Click += (_, __) => AcceptAll(ConflictSide.Theirs);

        DataContextChanged += OnDataContextChanged;
        Opened += (_, __) => { WireScrollSync(); EnsureBuilt(); };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _vm = DataContext as ConflictResolverWindowViewModel;
        if (_vm == null) return;
        _vm.GetMergedText = ReadResultText;
        _vm.ChunksReady += () => Avalonia.Threading.Dispatcher.UIThread.Post(EnsureBuilt);
        EnsureBuilt();
    }

    private void EnsureBuilt()
    {
        if (_vm == null || !_vm.IsChunkMode || _vm.Chunks.Count == 0) return;
        AttachChunkHandlers();
        BuildDocuments();
    }

    private void AttachChunkHandlers()
    {
        if (_chunkHandlersAttached || _vm == null) return;
        foreach (var chunk in _vm.Chunks)
            chunk.ResolutionChanged += OnChunkResolutionChanged;
        _chunkHandlersAttached = true;
    }

    private void OnChunkResolutionChanged()
    {
        if (_suppressRebuild) return;
        BuildDocuments();
    }

    private void OnGutterAction(MergeBand band, MergePane side, bool accept)
    {
        if (_vm == null) return;
        var chunk = _vm.Chunks[band.ChunkIndex];
        if (side == MergePane.Ours)
        {
            if (accept) chunk.ToggleAcceptOurs(); else chunk.ToggleRejectOurs();
        }
        else
        {
            if (accept) chunk.ToggleAcceptTheirs(); else chunk.ToggleRejectTheirs();
        }
    }

    // ---- Document construction (filler alignment) ----

    private static List<string> SplitLines(string text)
        => text.Length == 0 ? new List<string>() : text.Replace("\r\n", "\n").Split('\n').ToList();

    private void BuildDocuments()
    {
        if (_vm == null) return;

        var ours = new List<string>();
        var result = new List<string>();
        var theirs = new List<string>();
        _bands.Clear();
        _resultFillerLines.Clear();

        for (int i = 0; i < _vm.Chunks.Count; i++)
        {
            var c = _vm.Chunks[i];
            var oLines = SplitLines(c.OursText);
            var rLines = SplitLines(c.ResultText);
            var tLines = SplitLines(c.TheirsText);
            int h = Math.Max(oLines.Count, Math.Max(rLines.Count, tLines.Count));
            if (h == 0) continue;

            int start = ours.Count + 1;   // 1-based document line
            AppendWithFiller(ours, oLines, h);
            AppendWithFiller(result, rLines, h);
            AppendWithFiller(theirs, tLines, h);

            for (int f = rLines.Count; f < h; f++) _resultFillerLines.Add(start + f);

            _bands.Add(new MergeBand
            {
                ChunkIndex = i,
                Kind = c.Kind,
                IsConflict = c.IsConflict,
                Resolved = c.IsResolved,
                Ours = c.OursChoice,
                Theirs = c.TheirsChoice,
                StartLine = start,
                Height = h,
                OursLines = oLines.Count,
                ResultLines = rLines.Count,
                TheirsLines = tLines.Count,
            });
        }

        _settingText = true;
        SetDocText(OursEditor, string.Join("\n", ours));
        SetDocText(ResultEditor, string.Join("\n", result));
        SetDocText(TheirsEditor, string.Join("\n", theirs));
        _settingText = false;

        InvalidateRenderers();
    }

    private static void AppendWithFiller(List<string> list, List<string> lines, int height)
    {
        list.AddRange(lines);
        for (int i = lines.Count; i < height; i++) list.Add("");
    }

    private static void SetDocText(TextEditor editor, string text)
    {
        if (editor.Document == null) editor.Document = new AvaloniaEdit.Document.TextDocument();
        if (editor.Document.Text != text) editor.Document.Text = text;
    }

    private void InvalidateRenderers()
    {
        OursEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        ResultEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        TheirsEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
        OursGutter.InvalidateVisual();
        TheirsGutter.InvalidateVisual();
    }

    // ---- Reading the merged result (strip still-empty filler lines) ----

    private string ReadResultText()
    {
        var doc = ResultEditor.Document;
        if (doc == null) return "";
        var sb = new StringBuilder();
        bool any = false;
        foreach (var line in doc.Lines)
        {
            var text = doc.GetText(line);
            if (_resultFillerLines.Contains(line.LineNumber) && text.Trim().Length == 0)
                continue;   // filler gap we inserted for alignment
            if (any) sb.Append('\n');
            sb.Append(text);
            any = true;
        }
        return any ? sb.ToString() + "\n" : "";
    }

    private void AcceptAll(ConflictSide side)
    {
        if (_vm == null) return;
        _suppressRebuild = true;
        try
        {
            foreach (var c in _vm.Chunks.Where(c => c.IsConflict && !c.IsResolved))
            {
                if (side == ConflictSide.Ours) c.ForceOurs();
                else c.ForceTheirs();
            }
        }
        finally { _suppressRebuild = false; }
        BuildDocuments();
    }

    // ---- Editable result: capture typed edits as the conflict's custom resolution ----

    private void OnResultTextChanged(object? sender, EventArgs e)
    {
        if (_settingText || _vm == null) return;
        var caretLine = ResultEditor.TextArea.Caret.Line;
        var band = _bands.FirstOrDefault(b => b.IsConflict
            && caretLine >= b.StartLine && caretLine < b.StartLine + b.Height);
        if (band == null) return;

        var doc = ResultEditor.Document;
        var sb = new StringBuilder();
        for (int ln = band.StartLine; ln < band.StartLine + band.Height && ln <= doc.LineCount; ln++)
        {
            if (_resultFillerLines.Contains(ln)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(doc.GetText(doc.GetLineByNumber(ln)));
        }
        _vm.Chunks[band.ChunkIndex].SetCustomFromEditor(sb.ToString());
        _vm.NotifyResolvedByEdit();
    }

    // ---- Conflict navigation ----

    private void NavigateConflict(int dir)
    {
        var conflicts = _bands.Where(b => b.IsConflict).ToList();
        if (conflicts.Count == 0) return;
        _navIndex = ((_navIndex + dir) % conflicts.Count + conflicts.Count) % conflicts.Count;
        ResultEditor.ScrollToLine(conflicts[_navIndex].StartLine);
    }

    // ---- Synchronized scrolling ----

    private void WireScrollSync()
    {
        if (_scrollWired) return;
        _oursSv = OursEditor.FindDescendantOfType<ScrollViewer>();
        _resultSv = ResultEditor.FindDescendantOfType<ScrollViewer>();
        _theirsSv = TheirsEditor.FindDescendantOfType<ScrollViewer>();
        if (_oursSv == null || _resultSv == null || _theirsSv == null) return;

        _oursSv.ScrollChanged += (_, __) => SyncScroll(_oursSv);
        _resultSv.ScrollChanged += (_, __) => SyncScroll(_resultSv);
        _theirsSv.ScrollChanged += (_, __) => SyncScroll(_theirsSv);
        _scrollWired = true;
    }

    private void SyncScroll(ScrollViewer? source)
    {
        if (_syncingScroll || source == null) return;
        _syncingScroll = true;
        try
        {
            double y = source.Offset.Y;
            foreach (var sv in new[] { _oursSv, _resultSv, _theirsSv })
            {
                if (sv == null || ReferenceEquals(sv, source)) continue;
                if (Math.Abs(sv.Offset.Y - y) > 0.5)
                    sv.Offset = new Vector(sv.Offset.X, y);
            }
        }
        finally { _syncingScroll = false; }
        OursGutter.InvalidateVisual();
        TheirsGutter.InvalidateVisual();
    }
}

internal enum MergePane { Ours, Result, Theirs }

internal sealed class MergeBand
{
    public int ChunkIndex;
    public ChunkKind Kind;
    public bool IsConflict;
    public bool Resolved;
    public SideChoice Ours;
    public SideChoice Theirs;
    public int StartLine;   // 1-based
    public int Height;
    public int OursLines;
    public int ResultLines;
    public int TheirsLines;
}

// Draws per-band region backgrounds. Conflict bands are tinted across every pane (including the
// empty Result slot) so a conflict reads as one continuous highlighted row.
internal sealed class MergeBandRenderer : IBackgroundRenderer
{
    private readonly MergePane _pane;
    private readonly List<MergeBand> _bands;

    public MergeBandRenderer(MergePane pane, List<MergeBand> bands)
    {
        _pane = pane;
        _bands = bands;
    }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext dc)
    {
        if (_bands.Count == 0) return;
        textView.EnsureVisualLines();

        var conflict = ThemeBrush.Resolve("DiffRemovedBg", "#33191E");
        var resolved = ThemeBrush.Resolve("DiffAddedBg", "#11271B");
        var oursTint = ThemeBrush.Resolve("DiffAddedBg", "#11271B");
        var theirsTint = ThemeBrush.Resolve("AccentSelection", "#268B8BF5");
        var filler = ThemeBrush.Resolve("SurfaceCard", "#1A1E24");
        double width = textView.Bounds.Width;

        foreach (var vl in textView.VisualLines)
        {
            if (vl.FirstDocumentLine == null || vl.FirstDocumentLine.IsDeleted) continue;
            int ln = vl.FirstDocumentLine.LineNumber;
            var band = FindBand(ln);
            if (band == null) continue;

            IBrush? brush;
            if (band.IsConflict)
            {
                brush = band.Resolved ? resolved : conflict;   // whole band, all panes, incl. filler slot
            }
            else
            {
                int idx = ln - band.StartLine;
                int contentCount = _pane == MergePane.Ours ? band.OursLines
                                 : _pane == MergePane.Result ? band.ResultLines
                                 : band.TheirsLines;
                bool isFiller = idx >= contentCount;
                brush = isFiller ? filler : _pane switch
                {
                    MergePane.Ours => band.Kind == ChunkKind.LeftOnly ? oursTint : null,
                    MergePane.Theirs => band.Kind == ChunkKind.RightOnly ? theirsTint : null,
                    _ => null,
                };
            }

            if (brush != null)
                dc.DrawRectangle(brush, null, new Rect(0, vl.VisualTop, width, vl.Height));
        }
    }

    private MergeBand? FindBand(int line)
    {
        foreach (var b in _bands)
            if (line >= b.StartLine && line < b.StartLine + b.Height) return b;
        return null;
    }
}

// A gutter column between two panes that draws the accept-chevron + reject-✕ for its side of each
// conflict and turns clicks into resolution toggles. Positions map from the adjacent side pane's
// realized visual lines, so they stay aligned as the panes scroll.
public sealed class MergeGutter : Control
{
    private TextEditor? _editor;
    private List<MergeBand>? _bands;
    private MergePane _side;
    private Action<MergeBand, MergePane, bool>? _action;

    internal void Configure(TextEditor editor, List<MergeBand> bands, MergePane side,
        Action<MergeBand, MergePane, bool> action)
    {
        _editor = editor;
        _bands = bands;
        _side = side;
        _action = action;
    }

    public override void Render(DrawingContext context)
    {
        // Paint a full-bounds fill so the gutter blends with the card and is hit-testable.
        context.DrawRectangle(ThemeBrush.Resolve("SurfaceDeep", "#0B0D10"), null,
            new Rect(0, 0, Bounds.Width, Bounds.Height));
        if (_editor == null || _bands == null) return;

        var tv = _editor.TextArea.TextView;
        tv.EnsureVisualLines();
        double scroll = tv.ScrollOffset.Y;
        double w = Bounds.Width;

        var accentAvail = ThemeBrush.Resolve("AccentBrush", "#8B8BF5");
        var accepted = ThemeBrush.Resolve("SuccessBrush", "#42B968");
        var rejected = ThemeBrush.Resolve("DangerBrush", "#F87171");
        var muted = ThemeBrush.Resolve("TextMuted", "#8A93A6");
        var conflictBand = ThemeBrush.Resolve("DiffRemovedBg", "#33191E");
        var resolvedBand = ThemeBrush.Resolve("DiffAddedBg", "#11271B");

        foreach (var band in _bands.Where(b => b.IsConflict))
        {
            var vl = tv.GetVisualLine(band.StartLine);
            if (vl == null) continue;
            double y = vl.VisualTop - scroll;
            double h = vl.Height;

            // Continue the band tint across the gutter so a conflict reads as one unbroken row.
            context.DrawRectangle(band.Resolved ? resolvedBand : conflictBand, null,
                new Rect(0, y, w, h * band.Height));

            var choice = _side == MergePane.Ours ? band.Ours : band.Theirs;
            // Accept points toward the Result pane (» on the Ours gutter, « on the Theirs gutter).
            string acceptGlyph = _side == MergePane.Ours ? "»" : "«";
            var acceptBrush = choice == SideChoice.Accepted ? accepted : accentAvail;
            var rejectBrush = choice == SideChoice.Rejected ? rejected : muted;

            // Ours gutter: [ ✕ ][ » ]  ·  Theirs gutter: [ « ][ ✕ ]
            double acceptX = _side == MergePane.Ours ? w * 0.5 + 3 : 5;
            double rejectX = _side == MergePane.Ours ? 7 : w * 0.5 + 3;

            DrawGlyph(context, acceptGlyph, acceptX, y, h, acceptBrush, choice == SideChoice.Accepted, 20);
            DrawGlyph(context, "✕", rejectX, y, h, rejectBrush, choice == SideChoice.Rejected, 15);
        }
    }

    private static void DrawGlyph(DrawingContext dc, string glyph, double x, double y, double lineHeight,
        IBrush brush, bool active, double size)
    {
        var ft = new FormattedText(glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), size, brush);
        double gy = y + Math.Max(0, (lineHeight - ft.Height) / 2);
        if (active)
        {
            var pill = new Rect(x - 3, gy - 1, ft.Width + 6, ft.Height + 2);
            dc.DrawRectangle(new SolidColorBrush(Colors.Black, 0.30), null, pill, 4, 4);
        }
        dc.DrawText(ft, new Point(x, gy));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor == null || _bands == null || _action == null) return;

        var tv = _editor.TextArea.TextView;
        double scroll = tv.ScrollOffset.Y;
        var p = e.GetPosition(this);

        foreach (var band in _bands.Where(b => b.IsConflict))
        {
            var vl = tv.GetVisualLine(band.StartLine);
            if (vl == null) continue;
            double y = vl.VisualTop - scroll;
            if (p.Y < y || p.Y >= y + vl.Height) continue;

            // Accept is on the Result-facing half of the gutter.
            bool accept = _side == MergePane.Ours ? p.X > Bounds.Width / 2 : p.X < Bounds.Width / 2;
            _action(band, _side, accept);
            e.Handled = true;
            return;
        }
    }
}

internal static class ThemeBrush
{
    public static IBrush Resolve(string key, string fallback)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush b)
            return b;
        return new SolidColorBrush(Color.Parse(fallback));
    }
}
