using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitLoom.App.ViewModels;

public partial class MenuItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _header = string.Empty;

    // Issue #71: the label actually rendered for a tree row. Defaults to Header (kept in sync by
    // OnHeaderChanged below) but is overridden to just the last path segment (e.g. "foo" instead
    // of "feature/foo") when this item is nested under a folder node in a grouped branch tree —
    // Header itself must stay the branch's full friendly name since other call sites (menu text,
    // BuildRefMenu) depend on it.
    [ObservableProperty]
    private string _displayHeader = string.Empty;

    partial void OnHeaderChanged(string value) => DisplayHeader = value;

    [ObservableProperty]
    private bool _isCurrentBranch;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private ICommand? _command;

    [ObservableProperty]
    private object? _commandParameter;

    [ObservableProperty]
    private ObservableCollection<MenuItemViewModel> _subItems = new();

    // Issue #71: branch-tree grouping. A folder node (IsFolder = true) has no Command and its
    // actual children live in Children — kept separate from SubItems, which stays the per-branch
    // right-click/flyout action menu on leaf nodes.
    [ObservableProperty]
    private bool _isFolder;

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private ObservableCollection<MenuItemViewModel> _children = new();
}
