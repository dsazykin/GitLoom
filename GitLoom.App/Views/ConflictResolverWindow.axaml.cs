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
// panes; scrolling is kept in lock-step. Region backgrounds + gutter accept-chevrons are drawn by
// MergeBandRenderer. Resolution logic lives in the ViewModel/engine — this code-behind only renders
// the chunk state and turns clicks/edits into resolution calls.
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

        OursEditor.TextArea.TextView.PointerPressed += (s, e) => OnSidePaneClick(OursEditor, ConflictSide.Ours, e);
        TheirsEditor.TextArea.TextView.PointerPressed += (s, e) => OnSidePaneClick(TheirsEditor, ConflictSide.Theirs, e);
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

            // Result filler lines (content < band height) — skipped when reading the merged text.
            for (int f = rLines.Count; f < h; f++) _resultFillerLines.Add(start + f);

            _bands.Add(new MergeBand
            {
                ChunkIndex = i,
                Kind = c.Kind,
                IsConflict = c.IsConflict,
                Resolved = c.IsResolved,
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

    // ---- Accept interactions ----

    private void OnSidePaneClick(TextEditor editor, ConflictSide side, PointerPressedEventArgs e)
    {
        if (_vm == null) return;
        var textView = editor.TextArea.TextView;
        var p = e.GetPosition(textView);
        double documentY = p.Y + textView.ScrollOffset.Y;

        int? lineNo = LineAtDocumentY(textView, documentY);
        if (lineNo == null) return;

        var band = _bands.FirstOrDefault(b => b.IsConflict
            && lineNo.Value >= b.StartLine && lineNo.Value < b.StartLine + b.Height);
        if (band == null) return;

        var chunk = _vm.Chunks[band.ChunkIndex];
        if (side == ConflictSide.Ours) chunk.TakeOursCommand.Execute(null);
        else chunk.TakeTheirsCommand.Execute(null);
        e.Handled = true;
    }

    private static int? LineAtDocumentY(TextView textView, double documentY)
    {
        textView.EnsureVisualLines();
        foreach (var vl in textView.VisualLines)
        {
            if (vl.FirstDocumentLine == null || vl.FirstDocumentLine.IsDeleted) continue;
            if (documentY >= vl.VisualTop && documentY < vl.VisualTop + vl.Height)
                return vl.FirstDocumentLine.LineNumber;
        }
        return null;
    }

    private void AcceptAll(ConflictSide side)
    {
        if (_vm == null) return;
        _suppressRebuild = true;
        try
        {
            foreach (var c in _vm.Chunks.Where(c => c.IsConflict && !c.IsResolved))
            {
                if (side == ConflictSide.Ours) c.TakeOursCommand.Execute(null);
                else c.TakeTheirsCommand.Execute(null);
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

        // Read the (possibly-edited) lines of this band and record them as the Custom resolution.
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
    }
}

internal enum MergePane { Ours, Result, Theirs }

internal sealed class MergeBand
{
    public int ChunkIndex;
    public ChunkKind Kind;
    public bool IsConflict;
    public bool Resolved;
    public int StartLine;   // 1-based
    public int Height;
    public int OursLines;
    public int ResultLines;
    public int TheirsLines;
}

// Draws per-band region backgrounds and, on the side panes, gutter accept-chevrons.
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

        var conflict = Resolve("DiffRemovedBg", "#33191E");
        var resolved = Resolve("DiffAddedBg", "#11271B");
        var oursTint = Resolve("DiffAddedBg", "#11271B");
        var theirsTint = Resolve("AccentSelection", "#268B8BF5");
        var filler = Resolve("SurfaceCard", "#1A1E24");
        var chevronFill = Resolve("AccentBrush", "#8B8BF5");
        double width = textView.Bounds.Width;

        foreach (var vl in textView.VisualLines)
        {
            if (vl.FirstDocumentLine == null || vl.FirstDocumentLine.IsDeleted) continue;
            int ln = vl.FirstDocumentLine.LineNumber;
            var band = FindBand(ln);
            if (band == null) continue;

            int idx = ln - band.StartLine;
            int contentCount = _pane == MergePane.Ours ? band.OursLines
                             : _pane == MergePane.Result ? band.ResultLines
                             : band.TheirsLines;
            bool isFiller = idx >= contentCount;

            IBrush? brush = isFiller ? filler : BandBrush(band, conflict, resolved, oursTint, theirsTint);
            if (brush != null)
                dc.DrawRectangle(brush, null, new Rect(0, vl.VisualTop, width, vl.Height));
        }

        // Gutter accept-chevrons on the side panes.
        if (_pane == MergePane.Result) return;
        var glyph = _pane == MergePane.Ours ? "»" : "«";   // » / «
        foreach (var band in _bands.Where(b => b.IsConflict && !b.Resolved))
        {
            var vl = textView.GetVisualLine(band.StartLine);
            if (vl == null) continue;
            double y = vl.VisualTop;
            double x = _pane == MergePane.Ours ? width - 16 : 3;
            var ft = new FormattedText(glyph, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 13, chevronFill);
            dc.DrawText(ft, new Point(x, y + 1));
        }
    }

    private MergeBand? FindBand(int line)
    {
        foreach (var b in _bands)
            if (line >= b.StartLine && line < b.StartLine + b.Height) return b;
        return null;
    }

    private IBrush? BandBrush(MergeBand band, IBrush conflict, IBrush resolved, IBrush oursTint, IBrush theirsTint)
    {
        if (band.IsConflict)
            return band.Resolved ? resolved : conflict;
        return _pane switch
        {
            MergePane.Ours => band.Kind == ChunkKind.LeftOnly ? oursTint : null,
            MergePane.Theirs => band.Kind == ChunkKind.RightOnly ? theirsTint : null,
            _ => null,
        };
    }

    private static IBrush Resolve(string key, string fallback)
    {
        var app = Application.Current;
        if (app != null && app.TryGetResource(key, app.ActualThemeVariant, out var res) && res is IBrush b)
            return b;
        return new SolidColorBrush(Color.Parse(fallback));
    }
}
