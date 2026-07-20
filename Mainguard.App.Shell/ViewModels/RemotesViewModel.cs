using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Remotes-management panel (T-10): lists the repo's remotes and drives add / rename /
/// edit-URL / remove through <see cref="IGitService"/>. All validation and typed errors
/// come from the service; this VM only surfaces the messages. Hosted by RemotesWindow.
/// </summary>
public partial class RemotesViewModel : ViewModelBase
{
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly Action? _onChanged;

    public ObservableCollection<RemoteRowViewModel> Remotes { get; } = new();

    [ObservableProperty]
    private string _newRemoteName = string.Empty;

    [ObservableProperty]
    private string _newRemoteUrl = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public RemotesViewModel(IGitService git, string repoPath, Action? onChanged = null)
    {
        _git = git;
        _repoPath = repoPath;
        _onChanged = onChanged;
        Reload();
    }

    private void Reload()
    {
        Remotes.Clear();
        foreach (var r in _git.GetRemotes(_repoPath).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            Remotes.Add(new RemoteRowViewModel(r, this));
    }

    private bool CanAdd => !string.IsNullOrWhiteSpace(NewRemoteName) && !string.IsNullOrWhiteSpace(NewRemoteUrl);

    partial void OnNewRemoteNameChanged(string value) { ErrorMessage = null; AddRemoteCommand.NotifyCanExecuteChanged(); }
    partial void OnNewRemoteUrlChanged(string value) { ErrorMessage = null; AddRemoteCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddRemote()
    {
        if (Run(() => _git.AddRemote(_repoPath, NewRemoteName.Trim(), NewRemoteUrl.Trim())))
        {
            NewRemoteName = string.Empty;
            NewRemoteUrl = string.Empty;
        }
    }

    // Applies a row's pending rename and/or URL edit, then reloads.
    internal void SaveRow(RemoteRowViewModel row)
    {
        var ok = true;
        if (!string.Equals(row.Name.Trim(), row.OriginalName, StringComparison.Ordinal))
            ok = Run(() => _git.RenameRemote(_repoPath, row.OriginalName, row.Name.Trim()));

        if (ok && !string.Equals(row.Url.Trim(), row.OriginalUrl, StringComparison.Ordinal))
            ok = Run(() => _git.SetRemoteUrl(_repoPath, row.Name.Trim(), row.Url.Trim()));

        if (ok) Reload();
    }

    internal void RemoveRow(RemoteRowViewModel row)
    {
        if (Run(() => _git.RemoveRemote(_repoPath, row.OriginalName))) Reload();
    }

    // Runs a mutation, funnels any typed failure into ErrorMessage, and reloads on success.
    private bool Run(Action mutate)
    {
        try
        {
            mutate();
            ErrorMessage = null;
            _onChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One editable remote row: name + URL with their originals, and per-row save/remove.</summary>
public partial class RemoteRowViewModel : ViewModelBase
{
    private readonly RemotesViewModel _parent;

    public string OriginalName { get; }
    public string OriginalUrl { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _url;

    public RemoteRowViewModel(Mainguard.Git.Models.GitRemoteItem item, RemotesViewModel parent)
    {
        _parent = parent;
        OriginalName = item.Name;
        OriginalUrl = item.FetchUrl;
        _name = item.Name;
        _url = item.FetchUrl;
    }

    // Dirty iff the user edited the name or URL — drives the Save button's enablement.
    public bool IsDirty =>
        !string.Equals(Name?.Trim(), OriginalName, StringComparison.Ordinal)
        || !string.Equals(Url?.Trim(), OriginalUrl, StringComparison.Ordinal);

    partial void OnNameChanged(string value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnUrlChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save() => _parent.SaveRow(this);

    [RelayCommand]
    private void Remove() => _parent.RemoveRow(this);
}
