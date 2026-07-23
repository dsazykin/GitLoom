using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Mainguard.Agents.UI.Controls;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.UI.Controls;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.Views;

/// <summary>
/// Code-behind for <see cref="TerminalView"/>. Deliberately trivial: it instantiates the engine
/// control the <c>TerminalEngine</c> flag selects (interim <see cref="TerminalControl"/> or the
/// P2-18 <see cref="TerminalGridControl"/>), hands it to the <see cref="TerminalViewModel"/> as an
/// <c>ITerminalView</c>, and routes the control's layout-resize back to the VM. No VT parsing,
/// byte handling, or rendering logic lives here — that is a rejection trigger; it all sits in the
/// engine behind the interface, which is why the engine swap changes nothing in the ViewModel.
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly ITerminalEngineControl _engine;

    public TerminalView()
    {
        InitializeComponent();
        var (control, engine) = TerminalEngineSelection.CreateEngineControl();
        _engine = engine;
        if (this.FindControl<ContentControl>("TerminalHost") is { } host)
        {
            host.Content = control;
        }

        DataContextChanged += OnDataContextChanged;
        Bind();
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e) => Bind();

    private void Bind()
    {
        if (DataContext is not TerminalViewModel vm)
        {
            return;
        }

        vm.AttachView(_engine);
        _engine.UserResized -= OnUserResized;
        _engine.UserResized += OnUserResized;
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
