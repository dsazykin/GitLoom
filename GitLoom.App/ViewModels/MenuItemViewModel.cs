using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GitLoom.App.ViewModels;

public partial class MenuItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _header = string.Empty;

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
}
