using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private void OnListBoxPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var control = e.Source as Control;
        if (control != null && control.DataContext is CommitTimelineViewModel vm)
        {
            vm.SelectedCommit = null;
        }
    }
}
