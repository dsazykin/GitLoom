using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GitLoom.App.Views;

public partial class StagingPanelView : UserControl
{
    public StagingPanelView()
    {
        InitializeComponent();
    }

    private void StagedSelectAll_Checked(object? sender, RoutedEventArgs e)
    {
        StagedListBox.SelectAll();
    }

    private void StagedSelectAll_Unchecked(object? sender, RoutedEventArgs e)
    {
        StagedListBox.UnselectAll();
    }

    private void UnstagedSelectAll_Checked(object? sender, RoutedEventArgs e)
    {
        UnstagedListBox.SelectAll();
    }

    private void UnstagedSelectAll_Unchecked(object? sender, RoutedEventArgs e)
    {
        UnstagedListBox.UnselectAll();
    }
}
