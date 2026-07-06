using CommunityToolkit.Mvvm.ComponentModel;
using GitLoom.Core.Graph;
using GitLoom.Core.Models;

namespace GitLoom.App.ViewModels;

public partial class CommitRowViewModel : ObservableObject
{
    public GitCommitItem Commit { get; set; } = null!;
    public GraphNode Node { get; set; } = null!;

    [ObservableProperty]
    private bool _isHighlighted;

    // Signature verification (T-15). Set only when the timeline's signature column is on; the
    // badge in the row template toggles off these derived booleans. Default None → no badge.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignatureVerified))]
    [NotifyPropertyChangedFor(nameof(IsSignatureUntrusted))]
    [NotifyPropertyChangedFor(nameof(IsSignatureBad))]
    [NotifyPropertyChangedFor(nameof(HasSignatureBadge))]
    [NotifyPropertyChangedFor(nameof(SignatureTooltip))]
    private SignatureStatus _signatureStatus = SignatureStatus.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignatureTooltip))]
    private string _signatureSigner = string.Empty;

    /// <summary>Any signature present — collapses the badge holder (no reserved width) when false.</summary>
    public bool HasSignatureBadge => SignatureStatus != SignatureStatus.None;

    /// <summary>Good signature from a trusted key (git <c>G</c>) — the green verified badge.</summary>
    public bool IsSignatureVerified => SignatureStatus == SignatureStatus.Good;

    /// <summary>Signed but not fully verified (unknown validity, expired, can't-check) — amber badge.</summary>
    public bool IsSignatureUntrusted =>
        SignatureStatus is SignatureStatus.UnknownValidity
            or SignatureStatus.Expired
            or SignatureStatus.ExpiredKey
            or SignatureStatus.CannotCheck;

    /// <summary>Bad or revoked signature — the red warning badge.</summary>
    public bool IsSignatureBad =>
        SignatureStatus is SignatureStatus.Bad or SignatureStatus.Revoked;

    public string SignatureTooltip
    {
        get
        {
            var who = string.IsNullOrWhiteSpace(SignatureSigner) ? null : $" · {SignatureSigner}";
            return SignatureStatus switch
            {
                SignatureStatus.Good => $"Verified signature{who}",
                SignatureStatus.UnknownValidity => $"Signed — key validity unknown{who}",
                SignatureStatus.Expired => $"Signed — signature expired{who}",
                SignatureStatus.ExpiredKey => $"Signed — key expired{who}",
                SignatureStatus.CannotCheck => "Signed — cannot verify (public key missing)",
                SignatureStatus.Bad => "Bad signature — content does not match",
                SignatureStatus.Revoked => $"Signed — key revoked{who}",
                _ => "Not signed",
            };
        }
    }
}
