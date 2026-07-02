using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

public partial class ConflictResolverWindowViewModel : ObservableObject
{
    private string _filePath;
    private Avalonia.Controls.Window _window;

    public event Action? RequestNextConflict;
    public event Action? RequestPrevConflict;

    [ObservableProperty]
    private ObservableCollection<ConflictBlockViewModel> _blocks = new();

    [ObservableProperty]
    private GridLength _column1Width = new GridLength(1, GridUnitType.Star);

    [ObservableProperty]
    private GridLength _column2Width = new GridLength(1, GridUnitType.Star);

    [ObservableProperty]
    private GridLength _column3Width = new GridLength(1, GridUnitType.Star);

    [ObservableProperty]
    private bool _isFullyResolved;

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
                    OriginalLeftText = currentLeft.ToString(),
                    RightText = currentRight.ToString(),
                    OriginalRightText = currentRight.ToString(),
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

        CheckIfFullyResolved();
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
    private void NextConflict()
    {
        RequestNextConflict?.Invoke();
    }

    [RelayCommand]
    private void PrevConflict()
    {
        RequestPrevConflict?.Invoke();
    }

    [RelayCommand]
    private void DiscardAll()
    {
        foreach (var block in Blocks)
        {
            if (block.IsConflict)
            {
                block.IsLeftAccepted = false;
                block.IsRightAccepted = false;
                block.IsLeftDiscarded = true;
                block.IsRightDiscarded = true;
                block.FinalText = "";
            }
        }
        CheckIfFullyResolved();
    }

    [RelayCommand]
    private void Cancel()
    {
        _window.Close(false);
    }

    public void CheckIfFullyResolved()
    {
        IsFullyResolved = System.Linq.Enumerable.All(Blocks, b => !b.IsConflict || b.IsLeftAccepted || b.IsRightAccepted || (b.IsLeftDiscarded && b.IsRightDiscarded));
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
    private string _originalLeftText = "";

    [ObservableProperty]
    private string _originalRightText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftBackground))]
    [NotifyPropertyChangedFor(nameof(LeftButtonText))]
    [NotifyPropertyChangedFor(nameof(LeftDiscardVisible))]
    private bool _isLeftAccepted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightBackground))]
    [NotifyPropertyChangedFor(nameof(RightButtonText))]
    [NotifyPropertyChangedFor(nameof(RightDiscardVisible))]
    private bool _isRightAccepted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LeftKeepVisible))]
    [NotifyPropertyChangedFor(nameof(LeftDiscardText))]
    private bool _isLeftDiscarded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RightKeepVisible))]
    [NotifyPropertyChangedFor(nameof(RightDiscardText))]
    private bool _isRightDiscarded;

    public string LeftBackground => IsConflict ? (IsLeftAccepted ? "#333333" : "#283A2E") : "Transparent";
    public string MiddleBackground => IsConflict ? "#1E1E1E" : "Transparent";
    public string RightBackground => IsConflict ? (IsRightAccepted ? "#333333" : "#2B3645") : "Transparent";

    public string LeftButtonText => IsLeftAccepted ? "Undo" : "Keep & Move ->";
    public string RightButtonText => IsRightAccepted ? "Undo" : "<- Keep & Move";

    public bool LeftKeepVisible => !IsLeftDiscarded;
    public bool LeftDiscardVisible => !IsLeftAccepted;
    public string LeftDiscardText => IsLeftDiscarded ? "Undo" : "Discard";

    public bool RightKeepVisible => !IsRightDiscarded;
    public bool RightDiscardVisible => !IsRightAccepted;
    public string RightDiscardText => IsRightDiscarded ? "Undo" : "Discard";

    private bool _leftAcceptedFirst = true;

    [RelayCommand]
    private void AcceptLeft()
    {
        IsLeftAccepted = !IsLeftAccepted;
        if (IsLeftAccepted && !IsRightAccepted) _leftAcceptedFirst = true;
        RebuildFinalText();
    }

    [RelayCommand]
    private void AcceptRight()
    {
        IsRightAccepted = !IsRightAccepted;
        if (IsRightAccepted && !IsLeftAccepted) _leftAcceptedFirst = false;
        RebuildFinalText();
    }

    [RelayCommand]
    private void ClearFinal()
    {
        IsLeftAccepted = false;
        IsRightAccepted = false;
        FinalText = "";
    }

    [RelayCommand]
    private void DiscardLeft()
    {
        IsLeftDiscarded = !IsLeftDiscarded;
        if (IsLeftDiscarded)
        {
            IsLeftAccepted = false;
            LeftText = "";
        }
        else
        {
            LeftText = OriginalLeftText;
        }
        RebuildFinalText();
    }

    [RelayCommand]
    private void DiscardRight()
    {
        IsRightDiscarded = !IsRightDiscarded;
        if (IsRightDiscarded)
        {
            IsRightAccepted = false;
            RightText = "";
        }
        else
        {
            RightText = OriginalRightText;
        }
        RebuildFinalText();
    }

    private void RebuildFinalText()
    {
        FinalText = "";

        if (_leftAcceptedFirst)
        {
            if (IsLeftAccepted) FinalText += LeftText;
            if (IsRightAccepted)
            {
                if (FinalText.Length > 0 && !FinalText.EndsWith("\n") && RightText.Length > 0) FinalText += "\n";
                FinalText += RightText;
            }
        }
        else
        {
            if (IsRightAccepted) FinalText += RightText;
            if (IsLeftAccepted)
            {
                if (FinalText.Length > 0 && !FinalText.EndsWith("\n") && LeftText.Length > 0) FinalText += "\n";
                FinalText += LeftText;
            }
        }

        _parent.CheckIfFullyResolved();
    }
}
