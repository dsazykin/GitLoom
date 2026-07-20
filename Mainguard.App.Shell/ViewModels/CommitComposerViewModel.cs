using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Commits;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// T-31 conventional-commit composer. Holds the structured fields (type / scope / description / body /
/// breaking + description / co-authors / closes issue refs), assembles a live read-only
/// <see cref="Preview"/> through the pure <see cref="ConventionalCommitBuilder"/>, and surfaces
/// commitlint-style <see cref="Issues"/> (errors gate the commit, warnings are advisory). All message
/// assembly and validation lives in Core; this VM is a thin observable shell — severity colors are
/// chosen in the View from Danger/Warning tokens (no color in a VM).
/// </summary>
public partial class CommitComposerViewModel : ViewModelBase
{
    public CommitComposerViewModel()
    {
        Recompute();
    }

    /// <summary>The conventional-commit type set for the Type dropdown.</summary>
    public IReadOnlyList<string> Types { get; } = ConventionalCommitBuilder.Types;

    [ObservableProperty]
    private string _type = "feat";

    [ObservableProperty]
    private string _scope = "";

    [ObservableProperty]
    private string _description = "";

    [ObservableProperty]
    private string _body = "";

    [ObservableProperty]
    private bool _breaking;

    [ObservableProperty]
    private string _breakingDescription = "";

    /// <summary>Draft input for the "Add co-author" box ("Name &lt;email&gt;").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCoAuthorCommand))]
    private string _newCoAuthor = "";

    /// <summary>Draft input for the "Closes" issue-ref box ("#12", "org/repo#7").</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddIssueCommand))]
    private string _newIssue = "";

    public ObservableCollection<string> CoAuthors { get; } = new();
    public ObservableCollection<string> ClosesIssues { get; } = new();

    /// <summary>The live, read-only assembled commit message.</summary>
    [ObservableProperty]
    private string _preview = "";

    /// <summary>The commitlint-style validation findings (errors + advisory warnings).</summary>
    public ObservableCollection<CommitValidationIssue> Issues { get; } = new();

    /// <summary>True when any validation error is present — the staging panel disables the default Commit.</summary>
    public bool HasErrors => Issues.Any(i => i.IsError);

    public bool HasIssues => Issues.Count > 0;

    /// <summary>Subject-description length for the live counter (amber past the soft limit).</summary>
    public int DescriptionLength => (Description ?? "").Length;

    public bool DescriptionOverLimit => DescriptionLength > ConventionalCommitBuilder.SubjectSoftLimit;

    /// <summary>Raised whenever the assembled message or validation state changes (staging panel re-gates on it).</summary>
    public event System.Action? Changed;

    /// <summary>Snapshots the current fields into a pure draft.</summary>
    public ConventionalCommitDraft ToDraft() => new()
    {
        Type = Type ?? "",
        Scope = Scope ?? "",
        Description = Description ?? "",
        Body = Body ?? "",
        Breaking = Breaking,
        BreakingDescription = BreakingDescription ?? "",
        CoAuthors = CoAuthors.ToList(),
        ClosesIssues = ClosesIssues.ToList(),
    };

    private void Recompute()
    {
        var draft = ToDraft();
        Preview = ConventionalCommitBuilder.Build(draft);

        Issues.Clear();
        foreach (var issue in ConventionalCommitBuilder.Validate(draft)) Issues.Add(issue);

        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(DescriptionLength));
        OnPropertyChanged(nameof(DescriptionOverLimit));
        Changed?.Invoke();
    }

    partial void OnTypeChanged(string value) => Recompute();
    partial void OnScopeChanged(string value) => Recompute();
    partial void OnDescriptionChanged(string value) => Recompute();
    partial void OnBodyChanged(string value) => Recompute();
    partial void OnBreakingChanged(bool value) => Recompute();
    partial void OnBreakingDescriptionChanged(string value) => Recompute();

    private bool CanAddCoAuthor => !string.IsNullOrWhiteSpace(NewCoAuthor);

    [RelayCommand(CanExecute = nameof(CanAddCoAuthor))]
    private void AddCoAuthor()
    {
        var value = (NewCoAuthor ?? "").Trim();
        if (value.Length == 0 || CoAuthors.Contains(value)) { NewCoAuthor = ""; return; }
        CoAuthors.Add(value);
        NewCoAuthor = "";
        Recompute();
    }

    [RelayCommand]
    private void RemoveCoAuthor(string? coAuthor)
    {
        if (coAuthor is null) return;
        if (CoAuthors.Remove(coAuthor)) Recompute();
    }

    private bool CanAddIssue => !string.IsNullOrWhiteSpace(NewIssue);

    [RelayCommand(CanExecute = nameof(CanAddIssue))]
    private void AddIssue()
    {
        var value = (NewIssue ?? "").Trim();
        if (value.Length == 0 || ClosesIssues.Contains(value)) { NewIssue = ""; return; }
        ClosesIssues.Add(value);
        NewIssue = "";
        Recompute();
    }

    [RelayCommand]
    private void RemoveIssue(string? issue)
    {
        if (issue is null) return;
        if (ClosesIssues.Remove(issue)) Recompute();
    }

    /// <summary>Resets every field back to the empty default (after a successful commit).</summary>
    public void Clear()
    {
        Type = "feat";
        Scope = "";
        Description = "";
        Body = "";
        Breaking = false;
        BreakingDescription = "";
        NewCoAuthor = "";
        NewIssue = "";
        CoAuthors.Clear();
        ClosesIssues.Clear();
        Recompute();
    }
}
