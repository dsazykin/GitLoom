using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using AvaloniaEdit.TextMate;
using GitLoom.App.ViewModels;
using TextMateSharp.Grammars;

namespace GitLoom.App.Views;

public partial class DiffViewerView : UserControl
{
    private bool _isUpdatingFromViewModel;
    private TextMate.Installation _textMateInstallation;
    private RegistryOptions _registryOptions;
    private DiffMarginRenderer _diffRenderer;

    // Drag-select state for the partial-staging line gutter. On press we decide whether the
    // drag paints selection on or off (based on the first line's state), then apply it to
    // every change line the pointer passes over until release.
    private bool _isDraggingSelection;
    private bool _dragSelectValue;

    private void OnLinePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DiffLineRowViewModel line && line.IsChange)
        {
            _isDraggingSelection = true;
            _dragSelectValue = !line.IsSelected; // click toggles; drag paints this value
            line.IsSelected = _dragSelectValue;
        }
    }

    private void OnLinePointerEntered(object? sender, PointerEventArgs e)
    {
        if (_isDraggingSelection && (sender as Control)?.DataContext is DiffLineRowViewModel line && line.IsChange)
        {
            line.IsSelected = _dragSelectValue;
        }
    }

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
        => _isDraggingSelection = false;

    public DiffViewerView()
    {
        InitializeComponent();

        _registryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _textMateInstallation = Editor.InstallTextMate(_registryOptions);

        _diffRenderer = new DiffMarginRenderer();
        Editor.TextArea.TextView.BackgroundRenderers.Add(_diffRenderer);

        DataContextChanged += (s, e) =>
        {
            if (DataContext is DiffViewerViewModel vm)
            {
                if (Editor.Document == null)
                {
                    Editor.Document = new AvaloniaEdit.Document.TextDocument();
                }

                _isUpdatingFromViewModel = true;
                Editor.Document.Text = vm.RawContent ?? string.Empty;
                _isUpdatingFromViewModel = false;

                UpdateTextMate(vm.FilePath);
                _diffRenderer.ViewModel = vm;

                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(DiffViewerViewModel.RawContent))
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (Editor.Document == null)
                            {
                                Editor.Document = new AvaloniaEdit.Document.TextDocument();
                            }

                            _isUpdatingFromViewModel = true;
                            if (Editor.Document.Text != (vm.RawContent ?? string.Empty))
                            {
                                Editor.Document.Text = vm.RawContent ?? string.Empty;
                            }
                            _isUpdatingFromViewModel = false;

                            UpdateTextMate(vm.FilePath);
                            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
                        });
                    }
                };

                Editor.TextChanged += (sender, args) =>
                {
                    if (!_isUpdatingFromViewModel && Editor.Document != null)
                    {
                        vm.RawContent = Editor.Document.Text;
                    }
                };
            }
        };
    }

    private void UpdateTextMate(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            var ext = System.IO.Path.GetExtension(filePath);
            var langId = _registryOptions.GetLanguageByExtension(ext)?.Id;
            if (langId != null)
            {
                _textMateInstallation.SetGrammar(_registryOptions.GetScopeByLanguageId(langId));
            }
        }
    }
}

public class DiffMarginRenderer : IBackgroundRenderer
{
    public DiffViewerViewModel? ViewModel { get; set; }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (ViewModel == null) return;

        var addedLines = ViewModel.AddedLines;
        var modifiedLines = ViewModel.ModifiedLines;

        if (addedLines.Count == 0 && modifiedLines.Count == 0) return;

        // Resolve from the active theme so gutter bars follow theme switches.
        var addedBrush = ResolveThemeBrush("SuccessBrush", "#42B968");
        var modifiedBrush = ResolveThemeBrush("AccentBrush", "#8B8BF5");

        textView.EnsureVisualLines();
        foreach (var visualLine in textView.VisualLines)
        {
            if (visualLine.FirstDocumentLine == null || visualLine.FirstDocumentLine.IsDeleted) continue;
            int lineNumber = visualLine.FirstDocumentLine.LineNumber;

            bool isAdded = addedLines.Contains(lineNumber);
            bool isModified = modifiedLines.Contains(lineNumber);

            if (isAdded || isModified)
            {
                var brush = isModified ? modifiedBrush : addedBrush;

                // Draw a 4px wide bar on the left side of the text document space
                var rect = new Rect(0, visualLine.VisualTop, 4, visualLine.Height);
                drawingContext.DrawRectangle(brush, null, rect);
            }
        }
    }

    private static IBrush ResolveThemeBrush(string key, string fallback)
    {
        var app = Application.Current;
        if (app != null
            && app.TryGetResource(key, app.ActualThemeVariant, out var res)
            && res is IBrush brush)
        {
            return brush;
        }
        return new SolidColorBrush(Color.Parse(fallback));
    }
}
