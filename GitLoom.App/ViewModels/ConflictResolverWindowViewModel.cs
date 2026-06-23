using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

public partial class ConflictResolverWindowViewModel : ViewModelBase
{
    private readonly string _filePath;
    private readonly Window _window;
    
    [ObservableProperty]
    private ObservableCollection<ConflictBlockViewModel> _blocks = new();

    public string FileName => Path.GetFileName(_filePath);

    public ConflictResolverWindowViewModel(string filePath, Window window)
    {
        _filePath = filePath;
        _window = window;
        ParseFile();
    }

    private void ParseFile()
    {
        if (!File.Exists(_filePath)) return;
        var lines = File.ReadAllLines(_filePath);
        
        var currentCommon = new StringBuilder();
        var currentLeft = new StringBuilder();
        var currentRight = new StringBuilder();
        string leftLabel = "";
        string rightLabel = "";
        
        int state = 0; // 0 = common, 1 = left, 2 = right

        foreach (var line in lines)
        {
            if (line.StartsWith("<<<<<<<"))
            {
                if (currentCommon.Length > 0)
                {
                    Blocks.Add(new ConflictBlockViewModel(this) { IsConflict = false, CommonText = currentCommon.ToString() });
                    currentCommon.Clear();
                }
                leftLabel = line.Replace("<<<<<<<", "").Trim();
                state = 1;
            }
            else if (line.StartsWith("======="))
            {
                state = 2;
            }
            else if (line.StartsWith(">>>>>>>"))
            {
                rightLabel = line.Replace(">>>>>>>", "").Trim();
                Blocks.Add(new ConflictBlockViewModel(this) 
                { 
                    IsConflict = true, 
                    LeftText = currentLeft.ToString(), 
                    RightText = currentRight.ToString(),
                    LeftLabel = leftLabel,
                    RightLabel = rightLabel,
                    FinalText = "" 
                });
                currentLeft.Clear();
                currentRight.Clear();
                state = 0;
            }
            else
            {
                if (state == 0) currentCommon.AppendLine(line);
                else if (state == 1) currentLeft.AppendLine(line);
                else if (state == 2) currentRight.AppendLine(line);
            }
        }
        
        if (currentCommon.Length > 0)
        {
             Blocks.Add(new ConflictBlockViewModel(this) { IsConflict = false, CommonText = currentCommon.ToString() });
        }
    }

    [RelayCommand]
    private void SaveAndClose()
    {
        var sb = new StringBuilder();
        foreach (var block in Blocks)
        {
            if (block.IsConflict)
            {
                sb.Append(block.FinalText);
            }
            else
            {
                sb.Append(block.CommonText);
            }
        }
        File.WriteAllText(_filePath, sb.ToString());
        _window.Close(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _window.Close(false);
    }
}

public partial class ConflictBlockViewModel : ObservableObject
{
    private readonly ConflictResolverWindowViewModel _parent;

    public ConflictBlockViewModel(ConflictResolverWindowViewModel parent)
    {
        _parent = parent;
    }

    [ObservableProperty]
    private bool _isConflict;

    [ObservableProperty]
    private string _commonText = "";

    [ObservableProperty]
    private string _leftText = "";

    [ObservableProperty]
    private string _rightText = "";
    
    [ObservableProperty]
    private string _leftLabel = "";
    
    [ObservableProperty]
    private string _rightLabel = "";

    [ObservableProperty]
    private string _finalText = "";

    [RelayCommand]
    private void AcceptLeft()
    {
        FinalText += LeftText;
    }

    [RelayCommand]
    private void AcceptRight()
    {
        FinalText += RightText;
    }

    [RelayCommand]
    private void ClearFinal()
    {
        FinalText = "";
    }
}
