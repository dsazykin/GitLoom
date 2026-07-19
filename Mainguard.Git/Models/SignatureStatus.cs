namespace Mainguard.Git.Models;

/// <summary>
/// Verification state of a commit/tag GPG-or-SSH signature, mapped from git's
/// <c>%G?</c> pretty format (T-15). One value per <c>%G?</c> code; <see cref="None"/>
/// means the object carries no signature (git code <c>N</c>).
/// </summary>
public enum SignatureStatus
{
    /// <summary>No signature (git <c>N</c>).</summary>
    None,
    /// <summary>Good signature from a key of ultimate/known-good validity (git <c>G</c>).</summary>
    Good,
    /// <summary>Good signature, but the key's validity is unknown/marginal (git <c>U</c>).</summary>
    UnknownValidity,
    /// <summary>Bad signature — content does not match (git <c>B</c>).</summary>
    Bad,
    /// <summary>Good signature that has since expired (git <c>X</c>).</summary>
    Expired,
    /// <summary>Good signature made by a now-expired key (git <c>Y</c>).</summary>
    ExpiredKey,
    /// <summary>Good signature made by a revoked key (git <c>R</c>).</summary>
    Revoked,
    /// <summary>Cannot be checked — e.g. the public key is missing (git <c>E</c>).</summary>
    CannotCheck,
}

/// <summary>
/// The verification result for a single commit/tag: its <see cref="Status"/> plus the
/// signer identity git reports in <c>%GS</c> (may be empty when unsigned or unknown).
/// </summary>
public sealed record CommitSignatureInfo(SignatureStatus Status, string Signer)
{
    public static readonly CommitSignatureInfo None = new(SignatureStatus.None, string.Empty);

    /// <summary>A signature is present (anything other than <see cref="SignatureStatus.None"/>).</summary>
    public bool IsSigned => Status != SignatureStatus.None;

    /// <summary>The signature is present and fully verified good (git <c>G</c>).</summary>
    public bool IsVerified => Status == SignatureStatus.Good;
}

/// <summary>
/// One selectable signing key for the preferences key picker (T-15). <see cref="Id"/> is what
/// gets written to <c>user.signingkey</c> (a gpg key id/fingerprint, or an SSH public-key path);
/// <see cref="Label"/> is a human-readable description for the dropdown.
/// </summary>
public sealed record SigningKeyOption(string Id, string Label);
