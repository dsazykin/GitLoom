using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace GitLoom.Core.Models;

public class WorkspaceCategory : INotifyPropertyChanged
{
    public int CategoryId { get; set; }

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged();
            }
        }
    }

    public int DisplayOrder { get; set; }

    private bool _isExpanded = false;
    [NotMapped]
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isEditingName = false;
    [NotMapped]
    public bool IsEditingName
    {
        get => _isEditingName;
        set
        {
            if (_isEditingName != value)
            {
                _isEditingName = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isSelected = false;
    [NotMapped]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<Repository> Repositories { get; set; } = new ObservableCollection<Repository>();

    public int? ParentCategoryId { get; set; }
    public WorkspaceCategory? ParentCategory { get; set; }
    public ObservableCollection<WorkspaceCategory> SubCategories { get; set; } = new ObservableCollection<WorkspaceCategory>();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
