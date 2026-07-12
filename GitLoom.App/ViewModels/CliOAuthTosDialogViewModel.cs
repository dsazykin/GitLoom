using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core;
using GitLoom.Core.Models;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The CLI-OAuth ToS notice (P2-01 §4.2): states the Anthropic subscription-OAuth restriction enforced
/// 2026-04-04 and that the API-key path is the supported one. Acknowledging writes a persisted
/// <see cref="TosAcknowledgment"/> row <b>before</b> the CLI-OAuth option activates (invariant 4);
/// cancelling leaves it off. Constructed directly; the db factory is an injectable seam for tests.
/// </summary>
public partial class CliOAuthTosDialogViewModel : ViewModelBase
{
    private readonly Func<AppDbContext> _dbFactory;

    public string Provider { get; }

    /// <summary>True once the user acknowledged (and the row was written). The View reads this on close.</summary>
    public bool Acknowledged { get; private set; }

    /// <summary>Wired from the View so the dialog closes from the ViewModel; the bool is the result.</summary>
    public Action<bool>? CloseAction { get; set; }

    public string NoticeText =>
        "Anthropic restricted using a Claude.ai subscription through third-party tools via CLI OAuth " +
        "(enforced 4 April 2026). The supported path in GitLoom is a BYOK API key. If you proceed with " +
        "CLI OAuth you accept that it may stop working at any time and that you are responsible for " +
        "complying with Anthropic's terms.";

    public CliOAuthTosDialogViewModel(string provider = "anthropic", Func<AppDbContext>? dbFactory = null)
    {
        Provider = provider;
        _dbFactory = dbFactory ?? (() => new AppDbContext());
    }

    /// <summary>Records the acknowledgment (provider + timestamp) and closes with a positive result.</summary>
    [RelayCommand]
    private void Acknowledge()
    {
        using (var db = _dbFactory())
        {
            db.TosAcknowledgments.Add(new TosAcknowledgment
            {
                Provider = Provider,
                AcknowledgedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();
        }
        Acknowledged = true;
        CloseAction?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        Acknowledged = false;
        CloseAction?.Invoke(false);
    }
}
