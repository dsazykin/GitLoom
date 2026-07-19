using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using LibGit2Sharp;

namespace Mainguard.Git.Services;

/// <summary>
/// <see cref="IProfileService"/> backed by <see cref="AppDbContext"/> (SQLite) for persistence and
/// <see cref="IGitService.ExecuteWithRepo"/> for the one git touch-point — <see cref="Apply"/> writes
/// <b>local</b> repo config only. Each DB operation opens/disposes its own context via the injected
/// factory (the same short-lived-handle discipline the rest of the app uses); the default constructor
/// targets the app database and a real <see cref="GitService"/>, tests inject both.
/// </summary>
public sealed class ProfileService : IProfileService
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly IGitService _git;

    public ProfileService() : this(() => new AppDbContext(), new GitService()) { }

    public ProfileService(Func<AppDbContext> contextFactory, IGitService git)
    {
        _contextFactory = contextFactory;
        _git = git;
    }

    public IReadOnlyList<GitProfile> GetProfiles()
    {
        using var ctx = _contextFactory();
        return ctx.GitProfiles
            .OrderBy(p => p.Name.ToLower())
            .ToList();
    }

    public GitProfile? GetProfile(int id)
    {
        using var ctx = _contextFactory();
        return ctx.GitProfiles.FirstOrDefault(p => p.Id == id);
    }

    public GitProfile Create(GitProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        using var ctx = _contextFactory();
        EnsureNameFree(ctx, profile.Name, excludingId: null);

        // Let SQLite assign the identity; a caller-provided id is ignored on create.
        profile.Id = 0;
        ctx.GitProfiles.Add(profile);
        ctx.SaveChanges();
        return profile;
    }

    public void Update(GitProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        using var ctx = _contextFactory();
        var existing = ctx.GitProfiles.FirstOrDefault(p => p.Id == profile.Id)
            ?? throw new GitOperationException($"Profile {profile.Id} no longer exists.");
        EnsureNameFree(ctx, profile.Name, excludingId: profile.Id);

        existing.Name = profile.Name;
        existing.UserName = profile.UserName;
        existing.UserEmail = profile.UserEmail;
        existing.SignCommits = profile.SignCommits;
        existing.GpgFormat = profile.GpgFormat;
        existing.SigningKey = profile.SigningKey;
        existing.GpgProgram = profile.GpgProgram;
        ctx.SaveChanges();
    }

    public GitProfile? Delete(int id)
    {
        using var ctx = _contextFactory();
        var existing = ctx.GitProfiles.FirstOrDefault(p => p.Id == id);
        if (existing is null) return null;

        // Snapshot the removed row (preserving its id) so a "cancel"/Undo can re-create it verbatim.
        var snapshot = Clone(existing);
        ctx.GitProfiles.Remove(existing);
        ctx.SaveChanges();
        return snapshot;
    }

    public void Restore(GitProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        using var ctx = _contextFactory();
        if (ctx.GitProfiles.Any(p => p.Id == profile.Id)) return; // already restored — idempotent

        // Re-insert with the original id so references (e.g. a "default profile" preference) still resolve.
        ctx.GitProfiles.Add(Clone(profile));
        ctx.SaveChanges();
    }

    public void Apply(string repoPath, GitProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));

        // The single git touch-point goes through ExecuteWithRepo (deterministic handle lifetime).
        // Every write is ConfigurationLevel.Local — never Global/System (T-21 invariant).
        _git.ExecuteWithRepo(repoPath, repo =>
        {
            var cfg = repo.Config;
            cfg.Set("user.name", profile.UserName, ConfigurationLevel.Local);
            cfg.Set("user.email", profile.UserEmail, ConfigurationLevel.Local);

            if (profile.SignCommits)
            {
                cfg.Set("commit.gpgsign", true, ConfigurationLevel.Local);
                cfg.Set("tag.gpgsign", true, ConfigurationLevel.Local);
                if (!string.IsNullOrWhiteSpace(profile.GpgFormat))
                    cfg.Set("gpg.format", profile.GpgFormat, ConfigurationLevel.Local);
                if (!string.IsNullOrWhiteSpace(profile.SigningKey))
                    cfg.Set("user.signingkey", profile.SigningKey, ConfigurationLevel.Local);
                if (!string.IsNullOrWhiteSpace(profile.GpgProgram))
                    cfg.Set("gpg.program", profile.GpgProgram, ConfigurationLevel.Local);
            }
            else
            {
                cfg.Set("commit.gpgsign", false, ConfigurationLevel.Local);
                cfg.Set("tag.gpgsign", false, ConfigurationLevel.Local);
            }
        });
    }

    private static void EnsureNameFree(AppDbContext ctx, string name, int? excludingId)
    {
        var lowered = (name ?? string.Empty).Trim().ToLower();
        int exclude = excludingId ?? -1; // hoist out of the expression tree (SQLite can't translate ?? on Id)
        bool clash = ctx.GitProfiles.Any(p => p.Id != exclude && p.Name.ToLower() == lowered);
        if (clash) throw new DuplicateProfileNameException(name?.Trim() ?? string.Empty);
    }

    private static GitProfile Clone(GitProfile p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        UserName = p.UserName,
        UserEmail = p.UserEmail,
        SignCommits = p.SignCommits,
        GpgFormat = p.GpgFormat,
        SigningKey = p.SigningKey,
        GpgProgram = p.GpgProgram,
    };
}
