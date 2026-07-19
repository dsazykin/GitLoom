namespace Mainguard.Git.Models;

/// <summary>
/// A switchable Git identity / preference set (T-21). Applying a profile writes the identity and
/// signing preferences to a repository's <b>local</b> config only (never global) via
/// <see cref="Services.IProfileService.Apply"/>. Persisted in SQLite through <see cref="AppDbContext"/>
/// so profiles survive restarts, and named uniquely (case-insensitive) so the picker is unambiguous.
/// </summary>
public sealed class GitProfile
{
    public int Id { get; set; }

    /// <summary>User-facing profile label (e.g. "Work", "Open Source"). Unique, case-insensitive.</summary>
    public string Name { get; set; } = "";

    /// <summary>Value written to <c>user.name</c> when the profile is applied.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Value written to <c>user.email</c> when the profile is applied.</summary>
    public string UserEmail { get; set; } = "";

    /// <summary>When true, applying also turns on commit/tag signing (<c>commit.gpgsign</c>/<c>tag.gpgsign</c>).</summary>
    public bool SignCommits { get; set; }

    /// <summary>Signing format written to <c>gpg.format</c> — "openpgp" or "ssh" (T-15 vocabulary).</summary>
    public string GpgFormat { get; set; } = "openpgp";

    /// <summary>Value written to <c>user.signingkey</c> (a gpg key id or an SSH public-key path).</summary>
    public string SigningKey { get; set; } = "";

    /// <summary>Optional override for <c>gpg.program</c> (blank = git default).</summary>
    public string GpgProgram { get; set; } = "";
}
