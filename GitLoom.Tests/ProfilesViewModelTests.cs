using System.Collections.Generic;
using System.Linq;
using GitLoom.App.ViewModels;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-21 (profiles VM) — <see cref="ProfilesViewModel"/> against a fake <see cref="IProfileService"/>:
/// create/save, the duplicate-name error surfaced inline, the <b>cancel-safe delete</b> (delete removes
/// the row but keeps an Undo that restores it), and apply-to-repo routing.
/// </summary>
public class ProfilesViewModelTests
{
    private sealed class FakeProfileService : IProfileService
    {
        public readonly List<GitProfile> Store = new();
        public readonly List<(string repo, GitProfile profile)> Applied = new();
        private int _nextId = 1;

        public IReadOnlyList<GitProfile> GetProfiles() => Store.OrderBy(p => p.Name.ToLower()).ToList();
        public GitProfile? GetProfile(int id) => Store.FirstOrDefault(p => p.Id == id);

        public GitProfile Create(GitProfile profile)
        {
            if (Store.Any(p => p.Name.ToLower() == profile.Name.ToLower()))
                throw new DuplicateProfileNameException(profile.Name);
            profile.Id = _nextId++;
            Store.Add(profile);
            return profile;
        }

        public void Update(GitProfile profile)
        {
            if (Store.Any(p => p.Id != profile.Id && p.Name.ToLower() == profile.Name.ToLower()))
                throw new DuplicateProfileNameException(profile.Name);
            var existing = Store.First(p => p.Id == profile.Id);
            existing.Name = profile.Name;
            existing.UserName = profile.UserName;
            existing.UserEmail = profile.UserEmail;
        }

        public GitProfile? Delete(int id)
        {
            var p = Store.FirstOrDefault(x => x.Id == id);
            if (p is null) return null;
            Store.Remove(p);
            return p;
        }

        public void Restore(GitProfile profile)
        {
            if (Store.All(p => p.Id != profile.Id)) Store.Add(profile);
        }

        public void Apply(string repoPath, GitProfile profile) => Applied.Add((repoPath, profile));
    }

    private static void FillEditor(ProfilesViewModel vm, string name, string user = "A", string email = "a@b.c")
    {
        vm.EditName = name;
        vm.EditUserName = user;
        vm.EditUserEmail = email;
    }

    [Fact]
    public void New_ThenSave_ShouldAddProfileAndCloseEditor()
    {
        var svc = new FakeProfileService();
        var vm = new ProfilesViewModel(svc);

        vm.NewCommand.Execute(null);
        Assert.True(vm.IsEditing);
        FillEditor(vm, "Work");
        vm.SaveCommand.Execute(null);

        Assert.False(vm.IsEditing);
        Assert.Single(vm.Profiles);
        Assert.Equal("Work", vm.Profiles[0].Name);
    }

    [Fact]
    public void Save_WithBlankName_ShouldError_NotPersist()
    {
        var svc = new FakeProfileService();
        var vm = new ProfilesViewModel(svc);

        vm.NewCommand.Execute(null);
        FillEditor(vm, "   ");
        vm.SaveCommand.Execute(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(svc.Store);
        Assert.True(vm.IsEditing); // stays open for correction
    }

    [Fact]
    public void Save_WithDuplicateName_ShouldSurfaceInlineError()
    {
        var svc = new FakeProfileService();
        svc.Create(new GitProfile { Name = "Work", UserName = "A", UserEmail = "a@b.c" });
        var vm = new ProfilesViewModel(svc);

        vm.NewCommand.Execute(null);
        FillEditor(vm, "work"); // case-insensitive clash
        vm.SaveCommand.Execute(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("already exists", vm.ErrorMessage);
        Assert.Single(svc.Store);
    }

    [Fact]
    public void Delete_ShouldRemoveRow_ThenUndoRestoresIt()
    {
        var svc = new FakeProfileService();
        svc.Create(new GitProfile { Name = "Work", UserName = "A", UserEmail = "a@b.c" });
        var vm = new ProfilesViewModel(svc);

        var row = vm.Profiles.Single();
        row.DeleteCommand.Execute(null);

        Assert.Empty(vm.Profiles);          // removed immediately
        Assert.True(vm.CanUndoDelete);      // undo affordance shown
        Assert.NotNull(vm.UndoDeleteText);

        vm.UndoDeleteCommand.Execute(null);

        Assert.Single(vm.Profiles);         // cancel-safe: restored
        Assert.False(vm.CanUndoDelete);
        Assert.Equal("Work", vm.Profiles[0].Name);
    }

    [Fact]
    public void DismissUndo_ShouldClearUndoState_WithoutRestoring()
    {
        var svc = new FakeProfileService();
        svc.Create(new GitProfile { Name = "Work", UserName = "A", UserEmail = "a@b.c" });
        var vm = new ProfilesViewModel(svc);

        vm.Profiles.Single().DeleteCommand.Execute(null);
        vm.DismissUndoCommand.Execute(null);

        Assert.False(vm.CanUndoDelete);
        Assert.Empty(vm.Profiles); // stays deleted
    }

    [Fact]
    public void Apply_WithRepo_ShouldCallService()
    {
        var svc = new FakeProfileService();
        svc.Create(new GitProfile { Name = "Work", UserName = "A", UserEmail = "a@b.c" });
        var vm = new ProfilesViewModel(svc, repoPath: "/repo");

        Assert.True(vm.HasRepo);
        vm.Profiles.Single().ApplyCommand.Execute(null);

        Assert.Single(svc.Applied);
        Assert.Equal("/repo", svc.Applied[0].repo);
        Assert.NotNull(vm.StatusMessage);
    }

    [Fact]
    public void Apply_WithoutRepo_ShouldError_NotCallService()
    {
        var svc = new FakeProfileService();
        svc.Create(new GitProfile { Name = "Work", UserName = "A", UserEmail = "a@b.c" });
        var vm = new ProfilesViewModel(svc); // no repo

        Assert.False(vm.HasRepo);
        vm.Profiles.Single().ApplyCommand.Execute(null);

        Assert.Empty(svc.Applied);
        Assert.NotNull(vm.ErrorMessage);
    }
}
