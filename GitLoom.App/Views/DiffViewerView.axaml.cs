using Avalonia.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class DiffViewerView : UserControl
{
    private bool _isUpdatingFromViewModel;

    public DiffViewerView()
    {
        InitializeComponent();

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

                if (!string.IsNullOrEmpty(vm.FilePath))
                {
                    var ext = System.IO.Path.GetExtension(vm.FilePath);
                    Editor.SyntaxHighlighting = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinitionByExtension(ext);
                }

                vm.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(DiffViewerViewModel.RawContent))
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

                        if (!string.IsNullOrEmpty(vm.FilePath))
                        {
                            var ext = System.IO.Path.GetExtension(vm.FilePath);
                            Editor.SyntaxHighlighting = AvaloniaEdit.Highlighting.HighlightingManager.Instance.GetDefinitionByExtension(ext);
                        }
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
}
