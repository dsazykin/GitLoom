using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GitLoom.App.Controls;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Views;

public partial class CommitTimelineView : UserControl
{
    private ListBox? _commitsListBox;

    public CommitTimelineView()
    {
        InitializeComponent();

        this.AttachedToVisualTree += (s, e) =>
        {
            _commitsListBox = this.FindControl<ListBox>("CommitsListBox");
            if (_commitsListBox != null)
            {
                _commitsListBox.AddHandler(ScrollViewer.ScrollChangedEvent, OnScrollChanged, RoutingStrategies.Bubble);
                _commitsListBox.PointerPressed += OnListBoxPointerPressed;
                _commitsListBox.AddHandler(ContextRequestedEvent, OnContextRequested, RoutingStrategies.Bubble);
                _commitsListBox.KeyDown += OnListBoxKeyDown;
            }
        };
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (DataContext is CommitTimelineViewModel vm)
        {
            var scrollViewer = e.Source as ScrollViewer;
            if (scrollViewer != null)
            {
                if (scrollViewer.Offset.Y > 0 && scrollViewer.Offset.Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 500)
                {
                    vm.LoadMoreCommits();
                }
            }
        }
    }

    // Delete key on a selected ref label → delete the branch (through the confirmation dialog),
    // T-09 §3.5. SelectedRefName is armed when a label context menu is opened.
    private void OnListBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete
            && DataContext is CommitTimelineViewModel vm
            && !string.IsNullOrEmpty(vm.SelectedRefName)
            && vm.DeleteSelectedRefCommand.CanExecute(null))
        {
            vm.DeleteSelectedRefCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var control = e.Source as Control;
        if (control != null && control.DataContext is CommitTimelineViewModel vm)
        {
            vm.SelectedCommit = null;
        }
    }

    // Right-click on a commit row: run the pure hit-tester when the pointer is over the graph
    // canvas (Node vs. Label), otherwise treat the whole row as a commit hit. The menu itself is
    // built by the ViewModel (context rules live there, testably); this only renders it.
    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not CommitTimelineViewModel vm) return;
        if (e.Source is not Control source) return;

        var rowVm = FindRowViewModel(source);
        if (rowVm is null) return;

        GraphHit hit;
        var canvas = source.FindAncestorOfType<CommitGraphCanvas>(includeSelf: true);
        if (canvas is not null && e.TryGetPosition(canvas, out var pos))
        {
            hit = canvas.HitTest(pos);
        }
        else
        {
            hit = new GraphHit(GraphHitKind.Node, rowVm.Commit.Sha, null);
        }

        // Node/Label → its menu; empty graph space still yields the row's commit menu.
        var items = vm.BuildContextMenuForHit(hit) ?? vm.BuildCommitMenu(rowVm.Commit.Sha);
        if (items.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var menu = BuildContextMenu(items);
        menu.Open(source);
        e.Handled = true;
    }

    private static CommitRowViewModel? FindRowViewModel(Control? control)
    {
        while (control is not null)
        {
            if (control.DataContext is CommitRowViewModel row) return row;
            control = control.GetVisualParent() as Control;
        }
        return null;
    }

    private static ContextMenu BuildContextMenu(ObservableCollection<MenuItemViewModel> items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
        {
            menu.Items.Add(BuildMenuNode(item));
        }
        return menu;
    }

    private static Control BuildMenuNode(MenuItemViewModel vm)
    {
        if (vm is SeparatorViewModel)
        {
            return new Separator();
        }

        var menuItem = new MenuItem
        {
            Header = vm.Header,
            Command = vm.Command,
            CommandParameter = vm.CommandParameter,
            IsEnabled = vm.IsEnabled
        };

        foreach (var child in vm.SubItems)
        {
            menuItem.Items.Add(BuildMenuNode(child));
        }

        return menuItem;
    }
}
