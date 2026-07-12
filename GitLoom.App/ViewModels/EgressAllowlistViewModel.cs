using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The user-visible, editable default-deny egress allowlist (P2-07). Lists the hosts an agent may
/// reach through the proxy and drives add/remove through <see cref="IEgressAllowlistGateway"/> — the
/// daemon seam (edits are change-logged daemon-side). The App holds no container/egress engine
/// (ESC-I2/G-18). A git-host entry is marked as defeating A6 (it re-opens a direct route the daemon
/// git-proxy exists to remove).
/// </summary>
public partial class EgressAllowlistViewModel : ViewModelBase
{
    private readonly IEgressAllowlistGateway _gateway;

    public ObservableCollection<EgressAllowlistRowViewModel> Entries { get; } = new();

    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newHostPattern = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    public Action? CloseAction { get; set; }

    public EgressAllowlistViewModel(IEgressAllowlistGateway gateway)
    {
        _gateway = gateway;
        Reload();
    }

    /// <summary>True iff any entry re-opens a direct git-host route (A6 defeated) — shows the warning banner.</summary>
    public bool HasGitHostWarning => Entries.Any(e => e.DefeatsA6);

    private void Reload()
    {
        Entries.Clear();
        foreach (var item in _gateway.List().OrderBy(i => i.HostPattern, StringComparer.OrdinalIgnoreCase))
            Entries.Add(new EgressAllowlistRowViewModel(item, this));
        OnPropertyChanged(nameof(HasGitHostWarning));
    }

    private bool CanAdd => !string.IsNullOrWhiteSpace(NewName) && !string.IsNullOrWhiteSpace(NewHostPattern);

    partial void OnNewNameChanged(string value) { ErrorMessage = null; AddCommand.NotifyCanExecuteChanged(); }
    partial void OnNewHostPatternChanged(string value) { ErrorMessage = null; AddCommand.NotifyCanExecuteChanged(); }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        try
        {
            _gateway.Add(NewName.Trim(), NewHostPattern.Trim(), "Custom");
            NewName = string.Empty;
            NewHostPattern = string.Empty;
            Reload();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    internal void RemoveRow(EgressAllowlistRowViewModel row)
    {
        try
        {
            _gateway.Remove(row.HostPattern);
            Reload();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One allowlist row: the host, its category, the A6 marker, and per-row remove.</summary>
public partial class EgressAllowlistRowViewModel : ViewModelBase
{
    private readonly EgressAllowlistViewModel _parent;

    public string Name { get; }
    public string HostPattern { get; }
    public string Kind { get; }
    public bool DefeatsA6 { get; }

    public EgressAllowlistRowViewModel(EgressAllowlistItem item, EgressAllowlistViewModel parent)
    {
        _parent = parent;
        Name = item.Name;
        HostPattern = item.HostPattern;
        Kind = item.Kind;
        DefeatsA6 = item.DefeatsA6;
    }

    [RelayCommand]
    private void Remove() => _parent.RemoveRow(this);
}
