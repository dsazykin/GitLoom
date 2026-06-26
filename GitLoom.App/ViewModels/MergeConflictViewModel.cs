using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;

namespace GitLoom.App.ViewModels;

public partial class MergeConflictViewModel : ObservableObject
{
    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _leftText = string.Empty;

    [ObservableProperty]
    private string _middleText = string.Empty;

    [ObservableProperty]
    private string _rightText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MergeChunk> _chunks = new();

    public MergeConflictViewModel()
    {
    }

    public void LoadConflict(string filePath, string left, string middle, string right)
    {
        FilePath = filePath;
        LeftText = left;
        MiddleText = middle;
        RightText = right;
        // The real diffing service would be called here to populate the Chunks
    }

    [RelayCommand]
    private void AcceptLeft()
    {
        MiddleText = LeftText;
    }

    [RelayCommand]
    private void AcceptRight()
    {
        MiddleText = RightText;
    }

    [RelayCommand]
    private void Apply()
    {
        // Save MiddleText to disk and resolve conflict
    }

    [RelayCommand]
    private void Cancel()
    {
        // Close window without saving
    }
}
