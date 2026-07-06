using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// CRUD + apply for switchable Git identity profiles (T-21). Persistence is SQLite via
/// <see cref="AppDbContext"/>; <see cref="Apply"/> writes the profile's identity/signing settings to a
/// repository's <b>local</b> config only (never global). Delete is <i>cancel-safe</i>: it returns the
/// removed row so the caller can offer an Undo that <see cref="Restore"/> re-inserts verbatim.
/// </summary>
public interface IProfileService
{
    /// <summary>All profiles, ordered by name (case-insensitive).</summary>
    IReadOnlyList<GitProfile> GetProfiles();

    GitProfile? GetProfile(int id);

    /// <summary>Persists a new profile and returns it with its assigned <see cref="GitProfile.Id"/>.
    /// Throws <see cref="Exceptions.DuplicateProfileNameException"/> if the name is already taken.</summary>
    GitProfile Create(GitProfile profile);

    /// <summary>Saves edits to an existing profile. Throws
    /// <see cref="Exceptions.DuplicateProfileNameException"/> if the new name collides with another profile.</summary>
    void Update(GitProfile profile);

    /// <summary>Deletes the profile and returns the removed snapshot (an Undo token), or null if it did
    /// not exist. The snapshot preserves the original <see cref="GitProfile.Id"/> so <see cref="Restore"/>
    /// can re-create the exact row.</summary>
    GitProfile? Delete(int id);

    /// <summary>Re-inserts a profile removed by <see cref="Delete"/> — the "cancel delete" / Undo path.
    /// No-op if a profile with that id already exists again.</summary>
    void Restore(GitProfile profile);

    /// <summary>Writes the profile's identity (and, when enabled, signing) settings to the repository's
    /// <b>local</b> config — never global/system. Idempotent.</summary>
    void Apply(string repoPath, GitProfile profile);
}
