using Avalonia.Controls;
using AvaloniaEdit;

namespace GitLoom.App.Controls;

public partial class MergeEditorControl : UserControl
{
    public TextEditor? TextEditor => this.FindControl<TextEditor>("Editor");

    public MergeEditorControl()
    {
        InitializeComponent();
    }
}
