using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// External-PR-intake preferences (P2-12): the thin UI over the daemon intake config — the subscribed
/// sources, the bot-author allow-list, and the poll cadence. Subscriptions persist through the
/// <see cref="IPrIntakeStore"/> (idempotent add — a duplicate <c>(host, owner, repo, filter)</c> adds no
/// row); the author list and interval are the intake's configurable knobs (<see cref="ExternalPrIntake"/>).
///
/// <para>Constructed directly (no DI). The store is an injectable seam so the VM is fully unit-testable
/// offline. No daemon/host traffic happens here — this only edits configuration.</para>
/// </summary>
public partial class PrIntakeSettingsViewModel : ViewModelBase
{
    private readonly IPrIntakeStore _store;

    public ObservableCollection<PrIntakeSourceRowViewModel> Sources { get; } = new();

    [ObservableProperty]
    private string _newHost = "github.com";

    [ObservableProperty]
    private string _newOwner = string.Empty;

    [ObservableProperty]
    private string _newRepo = string.Empty;

    /// <summary>Optional per-source author filter; blank falls back to the shared bot list.</summary>
    [ObservableProperty]
    private string _newAuthorFilter = string.Empty;

    /// <summary>The shared bot-author allow-list, edited as a comma-separated list.</summary>
    [ObservableProperty]
    private string _botAuthors = string.Join(", ", ExternalPrIntake.DefaultBotAuthors);

    /// <summary>The poll cadence in seconds (mirrors <see cref="ExternalPrIntake.PollInterval"/>).</summary>
    [ObservableProperty]
    private int _pollIntervalSeconds = 60;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Wired from the View so Close works from the ViewModel.</summary>
    public Action? CloseAction { get; set; }

    public PrIntakeSettingsViewModel(IPrIntakeStore? store = null)
    {
        _store = store ?? new InMemoryPrIntakeStore();
        RefreshSources();
    }

    /// <summary>The parsed bot-author allow-list (empty entries dropped).</summary>
    public IReadOnlyList<string> ParsedBotAuthors =>
        BotAuthors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private bool CanAdd =>
        !string.IsNullOrWhiteSpace(NewHost)
        && !string.IsNullOrWhiteSpace(NewOwner)
        && !string.IsNullOrWhiteSpace(NewRepo);

    partial void OnNewHostChanged(string value) => AddSourceCommand.NotifyCanExecuteChanged();
    partial void OnNewOwnerChanged(string value) => AddSourceCommand.NotifyCanExecuteChanged();
    partial void OnNewRepoChanged(string value) => AddSourceCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddSource()
    {
        var filter = string.IsNullOrWhiteSpace(NewAuthorFilter) ? null : NewAuthorFilter.Trim();
        var source = new ExternalPrSource(NewHost.Trim(), NewOwner.Trim(), NewRepo.Trim(), filter);

        var added = _store.AddSubscription(source);
        StatusMessage = added
            ? $"Subscribed to {source.Key}."
            : $"{source.Key} is already subscribed.";

        NewOwner = string.Empty;
        NewRepo = string.Empty;
        NewAuthorFilter = string.Empty;
        RefreshSources();
    }

    private void RefreshSources()
    {
        Sources.Clear();
        foreach (var source in _store.Subscriptions())
        {
            Sources.Add(new PrIntakeSourceRowViewModel(source));
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One subscribed source row: its <c>host/owner/repo</c> and the author filter in effect.</summary>
public sealed class PrIntakeSourceRowViewModel
{
    public PrIntakeSourceRowViewModel(ExternalPrSource source)
    {
        Key = source.Key;
        AuthorFilter = string.IsNullOrWhiteSpace(source.AuthorFilter)
            ? "default bot list"
            : source.AuthorFilter!;
    }

    public string Key { get; }
    public string AuthorFilter { get; }
}
