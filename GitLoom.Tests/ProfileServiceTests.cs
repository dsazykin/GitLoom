using System;
using System.IO;
using System.Linq;
using GitLoom.Core;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

/// <summary>
/// TI-21 (profiles) — <see cref="ProfileService"/> CRUD, the duplicate-name guard, the cancel-safe
/// delete/restore round-trip, and the core invariant: <see cref="ProfileService.Apply"/> writes the
/// identity/signing settings to the repository's <b>local</b> config only, never global.
/// </summary>
public class ProfileServiceTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly string _dbPath;

    public ProfileServiceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "gitloom-profile-" + Guid.NewGuid().ToString("N") + ".db");
        using var ctx = new AppDbContext(_dbPath);
        ctx.Database.Migrate();
    }

    public void Dispose()
    {
        _fx.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private ProfileService NewService() => new(() => new AppDbContext(_dbPath), new GitService());

    private static GitProfile Sample(string name = "Work") => new()
    {
        Name = name,
        UserName = "Ada Lovelace",
        UserEmail = "ada@example.com",
    };

    [Fact]
    public void Create_ShouldPersistAndAssignId()
    {
        var svc = NewService();
        var created = svc.Create(Sample());

        Assert.True(created.Id > 0);
        Assert.Single(svc.GetProfiles());
        Assert.Equal("ada@example.com", svc.GetProfile(created.Id)!.UserEmail);
    }

    [Fact]
    public void GetProfiles_ShouldReturnCaseInsensitiveNameOrder()
    {
        var svc = NewService();
        svc.Create(Sample("Zeta"));
        svc.Create(Sample("alpha"));

        var names = svc.GetProfiles().Select(p => p.Name).ToList();
        Assert.Equal(new[] { "alpha", "Zeta" }, names);
    }

    [Fact]
    public void Create_WithDuplicateName_ShouldThrowTyped_CaseInsensitive()
    {
        var svc = NewService();
        svc.Create(Sample("Work"));

        var ex = Assert.Throws<DuplicateProfileNameException>(() => svc.Create(Sample("work")));
        Assert.Equal("work", ex.ProfileName);
        Assert.Single(svc.GetProfiles()); // the clashing create did not persist
    }

    [Fact]
    public void Update_ToAnotherProfilesName_ShouldThrow_ButRenameToOwnNameIsFine()
    {
        var svc = NewService();
        var a = svc.Create(Sample("Work"));
        var b = svc.Create(Sample("OSS"));

        b.Name = "Work";
        Assert.Throws<DuplicateProfileNameException>(() => svc.Update(b));

        // Renaming a profile keeping (a different) unique name works and persists edits.
        a.Name = "Work Main";
        a.UserEmail = "ada@work.dev";
        svc.Update(a);
        Assert.Equal("ada@work.dev", svc.GetProfile(a.Id)!.UserEmail);
        Assert.Equal("Work Main", svc.GetProfile(a.Id)!.Name);
    }

    [Fact]
    public void Delete_ThenRestore_ShouldRoundTrip_PreservingId()
    {
        var svc = NewService();
        var created = svc.Create(Sample("Work"));

        var snapshot = svc.Delete(created.Id);
        Assert.NotNull(snapshot);
        Assert.Equal(created.Id, snapshot!.Id);
        Assert.Empty(svc.GetProfiles());

        // The "cancel delete" / Undo path re-inserts the exact row (id preserved).
        svc.Restore(snapshot);
        var restored = svc.GetProfile(created.Id);
        Assert.NotNull(restored);
        Assert.Equal("Work", restored!.Name);
        Assert.Equal(created.Id, restored.Id);
    }

    [Fact]
    public void Delete_Missing_ShouldReturnNull()
    {
        var svc = NewService();
        Assert.Null(svc.Delete(999));
    }

    [Fact]
    public void Restore_WhenAlreadyPresent_ShouldBeIdempotent()
    {
        var svc = NewService();
        var created = svc.Create(Sample("Work"));

        svc.Restore(created); // profile still exists → no-op, no duplicate, no throw
        Assert.Single(svc.GetProfiles());
    }

    [Fact]
    public void Apply_ShouldWriteIdentityToLocalConfigOnly_GlobalUntouched()
    {
        var svc = NewService();
        var profile = new GitProfile { Name = "Work", UserName = "Grace Hopper", UserEmail = "grace@navy.mil" };

        // Snapshot the real global gitconfig (if any) to prove Apply never writes it.
        var globalPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gitconfig");
        var globalBefore = File.Exists(globalPath) ? File.ReadAllText(globalPath) : null;

        svc.Apply(_fx.RepoPath, profile);

        // The identity landed in the repository's LOCAL config file.
        var localConfigText = File.ReadAllText(Path.Combine(_fx.RepoPath, ".git", "config"));
        Assert.Contains("grace@navy.mil", localConfigText);
        Assert.Contains("Grace Hopper", localConfigText);

        using (var repo = new Repository(_fx.RepoPath))
        {
            Assert.Equal("Grace Hopper", repo.Config.Get<string>("user.name", ConfigurationLevel.Local)?.Value);
            Assert.Equal("grace@navy.mil", repo.Config.Get<string>("user.email", ConfigurationLevel.Local)?.Value);
            // Nothing was written at the Global level for this identity.
            Assert.NotEqual("grace@navy.mil", repo.Config.Get<string>("user.email", ConfigurationLevel.Global)?.Value);
        }

        var globalAfter = File.Exists(globalPath) ? File.ReadAllText(globalPath) : null;
        Assert.Equal(globalBefore, globalAfter); // global file byte-identical (or still absent)
    }

    [Fact]
    public void Apply_WithSigningOn_ShouldWriteSigningConfigLocally()
    {
        var svc = NewService();
        var profile = new GitProfile
        {
            Name = "Signed",
            UserName = "Ada",
            UserEmail = "ada@example.com",
            SignCommits = true,
            GpgFormat = "ssh",
            SigningKey = "/home/ada/.ssh/id_ed25519.pub",
        };

        svc.Apply(_fx.RepoPath, profile);

        using var repo = new Repository(_fx.RepoPath);
        Assert.True(repo.Config.Get<bool>("commit.gpgsign", ConfigurationLevel.Local)?.Value);
        Assert.Equal("ssh", repo.Config.Get<string>("gpg.format", ConfigurationLevel.Local)?.Value);
        Assert.Equal("/home/ada/.ssh/id_ed25519.pub", repo.Config.Get<string>("user.signingkey", ConfigurationLevel.Local)?.Value);
    }

    [Fact]
    public void Apply_WithSigningOff_ShouldDisableSigningLocally()
    {
        var svc = NewService();
        svc.Apply(_fx.RepoPath, new GitProfile { Name = "S", UserName = "A", UserEmail = "a@b.c", SignCommits = true, SigningKey = "K" });
        svc.Apply(_fx.RepoPath, new GitProfile { Name = "P", UserName = "A", UserEmail = "a@b.c", SignCommits = false });

        using var repo = new Repository(_fx.RepoPath);
        Assert.False(repo.Config.Get<bool>("commit.gpgsign", ConfigurationLevel.Local)?.Value);
    }
}
