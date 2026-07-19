using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using GitLoom.App.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

/// <summary>
/// Code-behind for <see cref="TerminalView"/>. Deliberately trivial: it only binds the concrete
/// <see cref="TerminalControl"/> engine to the <see cref="TerminalViewModel"/> (hand the VM the
/// control as an <c>ITerminalView</c>, and route the control's layout-resize back to the VM). No VT
/// parsing, byte handling, or rendering logic lives here — that is a rejection trigger; it all sits
/// in the engine behind the interface.
/// </summary>
public partial class TerminalView : UserControl
{
    private TerminalControl? _terminal;

    public TerminalView()
    {
        InitializeComponent();
        _terminal = this.FindControl<TerminalControl>("Terminal");
        DataContextChanged += OnDataContextChanged;
        Bind();
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e) => Bind();

    private void Bind()
    {
        if (_terminal is null || DataContext is not TerminalViewModel vm)
        {
            return;
        }

        vm.AttachView(_terminal);
        _terminal.UserResized -= OnUserResized;
        _terminal.UserResized += OnUserResized;
    }

    private void OnUserResized(object? sender, TerminalResizeEventArgs e)
    {
        if (DataContext is TerminalViewModel vm)
        {
            vm.OnUserResize(e.Cols, e.Rows);
        }
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
