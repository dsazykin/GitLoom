using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// Profiles manager (T-21): CRUD over switchable Git identities plus "Apply to this repo" (writes the
/// profile's identity/signing settings to the open repository's <b>local</b> config through
/// <see cref="IProfileService"/>). Delete is <b>cancel-safe</b>: the row is removed immediately but the
/// removed snapshot is retained so <see cref="UndoDeleteCommand"/> re-inserts it verbatim until the user
/// dismisses the toast or deletes again. All git/DB work is synchronous-but-cheap; typed failures
/// (e.g. duplicate name) surface as <see cref="ErrorMessage"/>. Hosted by the Settings "Git Profiles"
/// page (<c>ProfilesPageView</c>/<c>ProfilesPageViewModel</c>).
/// </summary>
public partial class ProfilesViewModel : ViewModelBase
{
    private readonly IProfileService _profiles;
    private readonly string? _repoPath;

    public ObservableCollection<ProfileRowViewModel> Profiles { get; } = new();

    /// <summary>True when a repository is open, enabling "Apply to this repo".</summary>
    public bool HasRepo => !string.IsNullOrEmpty(_repoPath);

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private bool _isNew;

    // --- Editor form fields (bound while IsEditing).
    [ObservableProperty]
    private string _editName = "";
    [ObservableProperty]
    private string _editUserName = "";
    [ObservableProperty]
    private string _editUserEmail = "";
    [ObservableProperty]
    private bool _editSignCommits;
    [ObservableProperty]
    private string _editGpgFormat = "openpgp";
    [ObservableProperty]
    private string _editSigningKey = "";
    [ObservableProperty]
    private string _editGpgProgram = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    // Cancel-safe delete state: the removed snapshot + the undo affordance.
    private GitProfile? _pendingDeleted;
    [ObservableProperty]
    private bool _canUndoDelete;
    [ObservableProperty]
    private string? _undoDeleteText;

    private int _editingId; // 0 while adding a new profile

    public Action? CloseAction { get; set; }

    public ProfilesViewModel(IProfileService profiles, string? repoPath = null)
    {
        _profiles = profiles;
        _repoPath = repoPath;
        Reload();
    }

    private void Reload()
    {
        Profiles.Clear();
        foreach (var p in _profiles.GetProfiles())
            Profiles.Add(new ProfileRowViewModel(p, this));
        OnPropertyChanged(nameof(HasRepo));
    }

    [RelayCommand]
    private void New()
    {
        _editingId = 0;
        IsNew = true;
        EditName = "";
        EditUserName = "";
        EditUserEmail = "";
        EditSignCommits = false;
        EditGpgFormat = "openpgp";
        EditSigningKey = "";
        EditGpgProgram = "";
        ErrorMessage = null;
        IsEditing = true;
    }

    internal void BeginEdit(GitProfile p)
    {
        _editingId = p.Id;
        IsNew = false;
        EditName = p.Name;
        EditUserName = p.UserName;
        EditUserEmail = p.UserEmail;
        EditSignCommits = p.SignCommits;
        EditGpgFormat = string.IsNullOrEmpty(p.GpgFormat) ? "openpgp" : p.GpgFormat;
        EditSigningKey = p.SigningKey;
        EditGpgProgram = p.GpgProgram;
        ErrorMessage = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ErrorMessage = "A profile name is required.";
            return;
        }

        var profile = new GitProfile
        {
            Id = _editingId,
            Name = EditName.Trim(),
            UserName = EditUserName.Trim(),
            UserEmail = EditUserEmail.Trim(),
            SignCommits = EditSignCommits,
            GpgFormat = EditGpgFormat,
            SigningKey = EditSigningKey.Trim(),
            GpgProgram = EditGpgProgram.Trim(),
        };

        try
        {
            if (_editingId == 0) _profiles.Create(profile);
            else _profiles.Update(profile);
        }
        catch (DuplicateProfileNameException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        catch (MainguardException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        IsEditing = false;
        ErrorMessage = null;
        Reload();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        ErrorMessage = null;
    }

    // Cancel-safe delete: remove now, keep the snapshot so Undo can restore it.
    internal void Delete(ProfileRowViewModel row)
    {
        var snapshot = _profiles.Delete(row.Model.Id);
        if (snapshot is null) { Reload(); return; }

        _pendingDeleted = snapshot;
        CanUndoDelete = true;
        UndoDeleteText = $"Deleted \"{snapshot.Name}\".";
        Reload();
    }

    [RelayCommand]
    private void UndoDelete()
    {
        if (_pendingDeleted is null) return;
        _profiles.Restore(_pendingDeleted);
        _pendingDeleted = null;
        CanUndoDelete = false;
        UndoDeleteText = null;
        Reload();
    }

    [RelayCommand]
    private void DismissUndo()
    {
        _pendingDeleted = null;
        CanUndoDelete = false;
        UndoDeleteText = null;
    }

    // Apply the profile's identity/signing to the open repo's LOCAL config.
    internal void Apply(ProfileRowViewModel row)
    {
        if (!HasRepo || _repoPath is null)
        {
            ErrorMessage = "Open a repository to apply a profile.";
            return;
        }
        try
        {
            _profiles.Apply(_repoPath, row.Model);
            StatusMessage = $"Applied \"{row.Model.Name}\" to this repository.";
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One profile row in the list: display identity + per-row Apply / Edit / Delete affordances.</summary>
public partial class ProfileRowViewModel : ViewModelBase
{
    private readonly ProfilesViewModel _parent;

    public GitProfile Model { get; }

    public ProfileRowViewModel(GitProfile model, ProfilesViewModel parent)
    {
        Model = model;
        _parent = parent;
    }

    public string Name => Model.Name;
    public string Identity =>
        string.IsNullOrEmpty(Model.UserEmail) ? Model.UserName : $"{Model.UserName} <{Model.UserEmail}>";
    public bool SignsCommits => Model.SignCommits;
    public bool CanApply => _parent.HasRepo;

    [RelayCommand]
    private void Apply() => _parent.Apply(this);

    [RelayCommand]
    private void Edit() => _parent.BeginEdit(Model);

    [RelayCommand]
    private void Delete() => _parent.Delete(this);
}
