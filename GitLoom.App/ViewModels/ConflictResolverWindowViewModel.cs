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
                    string commonText = currentCommon.ToString();
                    Blocks.Add(new ConflictBlockViewModel(this) 
                    { 
                        IsConflict = false, 
                        CommonText = commonText,
                        LeftText = commonText,
                        RightText = commonText,
                        FinalText = commonText
                    });
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
            string commonText = currentCommon.ToString();
            Blocks.Add(new ConflictBlockViewModel(this) 
            { 
                IsConflict = false, 
                CommonText = commonText,
                LeftText = commonText,
                RightText = commonText,
                FinalText = commonText
            });
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftBackground))]
    [NotifyPropertyChangedFor(nameof(LeftButtonText))]
    private bool _isLeftAccepted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightBackground))]
    [NotifyPropertyChangedFor(nameof(RightButtonText))]
    private bool _isRightAccepted;

    public string LeftBackground => IsConflict ? (IsLeftAccepted ? "#333333" : "#283A2E") : "Transparent";
    public string MiddleBackground => IsConflict ? "#1E1E1E" : "Transparent";
    public string RightBackground => IsConflict ? (IsRightAccepted ? "#333333" : "#2B3645") : "Transparent";

    public string LeftButtonText => IsLeftAccepted ? "Undo" : "Keep & Move ->";
    public string RightButtonText => IsRightAccepted ? "Undo" : "<- Keep & Move";

    [RelayCommand]
    private void AcceptLeft()
    {
        IsLeftAccepted = !IsLeftAccepted;
        RebuildFinalText();
    }

    [RelayCommand]
    private void AcceptRight()
    {
        IsRightAccepted = !IsRightAccepted;
        RebuildFinalText();
    }

    [RelayCommand]
    private void ClearFinal()
    {
        IsLeftAccepted = false;
        IsRightAccepted = false;
        RebuildFinalText();
    }

    private void RebuildFinalText()
    {
        FinalText = "";
        if (IsLeftAccepted)
        {
            FinalText += LeftText;
        }
        if (IsRightAccepted)
        {
            if (FinalText.Length > 0 && !FinalText.EndsWith("\n") && RightText.Length > 0) FinalText += "\n";
            FinalText += RightText;
        }
    }
}
