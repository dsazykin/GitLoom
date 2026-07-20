using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace Mainguard.Git.Models;

public class Repository : INotifyPropertyChanged
{
    public int RepositoryId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;

    private string? _customIconColor = "#569CD6";
    public string? CustomIconColor
    {
        get => _customIconColor;
        set
        {
            if (_customIconColor != value)
            {
                _customIconColor = value;
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

    public int CategoryId { get; set; }

    // Navigation property
    public WorkspaceCategory? Category { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
