using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Commits;
using Mainguard.Git.Models;
using Mainguard.Git.Safety;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.ViewModels;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Tests;

/// <summary>
/// TI-31 — the conventional-commit composer VM. The preview + validation update live from the fields;
/// co-author/issue chips add and remove; errors gate commit content. Pure — no Avalonia app needed.
/// </summary>
public sealed class CommitComposerViewModelTests
{
    [Fact]
    public void Preview_UpdatesLive_AsFieldsChange()
    {
        var vm = new CommitComposerViewModel { Type = "feat", Description = "add dark mode" };
        Assert.Equal("feat: add dark mode", vm.Preview);

        vm.Scope = "ui";
        Assert.Equal("feat(ui): add dark mode", vm.Preview);

        vm.Breaking = true;
        Assert.StartsWith("feat(ui)!: add dark mode", vm.Preview);
    }

    [Fact]
    public void Issues_ReflectValidation_UnknownTypeAndEmptyDescription()
    {
        var vm = new CommitComposerViewModel { Type = "banana", Description = "" };
        Assert.True(vm.HasErrors);
        Assert.Contains(vm.Issues, i => i.IsError && i.Field == "Type");
        Assert.Contains(vm.Issues, i => i.IsError && i.Field == "Description");
    }

    [Fact]
    public void HasErrors_ClearsOnceValid()
    {
        var vm = new CommitComposerViewModel { Type = "feat", Description = "" };
        Assert.True(vm.HasErrors);

        vm.Description = "add a thing";
        Assert.False(vm.HasErrors);
        Assert.Empty(vm.Issues);
    }

    [Fact]
    public void AddAndRemoveCoAuthor_UpdatesPreview()
    {
        var vm = new CommitComposerViewModel { Type = "chore", Description = "bump deps" };
        vm.NewCoAuthor = "Jane Doe <jane@example.com>";
        vm.AddCoAuthorCommand.Execute(null);

        Assert.Contains("Jane Doe <jane@example.com>", vm.CoAuthors);
        Assert.Equal("", vm.NewCoAuthor);
        Assert.Contains("Co-authored-by: Jane Doe <jane@example.com>", vm.Preview);

        vm.RemoveCoAuthorCommand.Execute("Jane Doe <jane@example.com>");
        Assert.Empty(vm.CoAuthors);
        Assert.DoesNotContain("Co-authored-by:", vm.Preview);
    }

    [Fact]
    public void AddIssue_AppearsInPreview_AsClosesTrailer()
    {
        var vm = new CommitComposerViewModel { Type = "fix", Description = "handle nulls" };
        vm.NewIssue = "#42";
        vm.AddIssueCommand.Execute(null);

        Assert.Contains("#42", vm.ClosesIssues);
        Assert.Contains("Closes #42", vm.Preview);
    }

    [Fact]
    public void DescriptionOverLimit_TogglesPastSoftLimit()
    {
        var vm = new CommitComposerViewModel { Type = "feat", Description = "short" };
        Assert.False(vm.DescriptionOverLimit);

        vm.Description = new string('x', ConventionalCommitBuilder.SubjectSoftLimit + 5);
        Assert.True(vm.DescriptionOverLimit);
        Assert.Equal(ConventionalCommitBuilder.SubjectSoftLimit + 5, vm.DescriptionLength);
    }

    [Fact]
    public void Changed_Fires_WhenAFieldChanges()
    {
        var vm = new CommitComposerViewModel { Type = "feat", Description = "x" };
        var fired = 0;
        vm.Changed += () => fired++;

        vm.Description = "add a thing";
        Assert.True(fired > 0);
    }

    [Fact]
    public void Clear_ResetsAllFields()
    {
        var vm = new CommitComposerViewModel { Type = "fix", Scope = "core", Description = "guard nulls", Breaking = true };
        vm.NewCoAuthor = "Ann <ann@x.io>";
        vm.AddCoAuthorCommand.Execute(null);

        vm.Clear();

        Assert.Equal("feat", vm.Type);
        Assert.Equal("", vm.Scope);
        Assert.Equal("", vm.Description);
        Assert.False(vm.Breaking);
        Assert.Empty(vm.CoAuthors);
    }
}

/// <summary>
/// TI-31 — the staging panel's use of the composer: the plain⇄structured toggle persists, a structured
/// commit uses the assembled message (routed through the existing commit path), and the T-30 pre-commit
/// scan still gates a structured commit. Real journal-free GitService over a fixture repo.
/// </summary>
[Trait("Category", "RequiresGitCli")]
public sealed class StagingPanelComposerTests : IDisposable
{
    private const string PlantedAwsKey = "AKIAIOSFODNN7EXAMPLE";

    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    private string HeadMessage()
    {
        using var repo = new Repository(_fx.RepoPath);
        return repo.Head.Tip?.Message ?? "";
    }

    private int CommitCount()
    {
        using var repo = new Repository(_fx.RepoPath);
        return repo.Head.Tip == null ? 0 : repo.Commits.Count();
    }

    private StagingPanelViewModel Staging(UserPreferences prefs, SettingsService? settings = null)
    {
        var vm = new StagingPanelViewModel(_git, _fx.RepoPath, onCommitAction: () => { },
            showNotification: null,
            scanner: new PreCommitScanner(_git),
            preferences: () => prefs,
            settings: settings);
        vm.UpdateStatus(_git.GetRepositoryStatus(_fx.RepoPath));
        return vm;
    }

    [Fact]
    public void UseStructuredComposer_TogglePersistsToSettings()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mainguard-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(tempFile);
            var vm = Staging(settings.Current, settings);

            Assert.False(vm.UseStructuredComposer);   // default plain
            vm.UseStructuredComposer = true;
            Assert.True(settings.Current.UseStructuredCommitComposer);

            // A fresh SettingsService over the same file sees the persisted choice.
            Assert.True(new SettingsService(tempFile).Current.UseStructuredCommitComposer);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task Commit_InStructuredMode_UsesAssembledMessage()
    {
        _fx.WriteFile("notes.txt", "hello\n");
        _git.StageFile(_fx.RepoPath, "notes.txt");

        var prefs = new UserPreferences { UseStructuredCommitComposer = true, PreCommitScanEnabled = false };
        var vm = Staging(prefs);
        vm.CommitComposer.Type = "feat";
        vm.CommitComposer.Scope = "api";
        vm.CommitComposer.Description = "add pagination";
        vm.CommitComposer.NewIssue = "#7";
        vm.CommitComposer.AddIssueCommand.Execute(null);

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.Equal(1, CommitCount());
        var message = HeadMessage();
        Assert.Contains("feat(api): add pagination", message);
        Assert.Contains("Closes #7", message);
    }

    [Fact]
    public async Task Commit_InStructuredMode_StillGatedByPreCommitScan()
    {
        _fx.WriteFile("creds.env", $"AWS_ACCESS_KEY_ID={PlantedAwsKey}\n");
        _git.StageFile(_fx.RepoPath, "creds.env");

        var prefs = new UserPreferences { UseStructuredCommitComposer = true, PreCommitScanEnabled = true };
        var vm = Staging(prefs);
        vm.CommitComposer.Type = "feat";
        vm.CommitComposer.Description = "add credentials";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.PreCommitFindings.AwaitingOverride);   // blocker paused the structured commit
        Assert.Equal(0, CommitCount());

        await vm.PreCommitFindings.CommitAnywayCommand.ExecuteAsync(null);

        Assert.Equal(1, CommitCount());
        Assert.Contains("feat: add credentials", HeadMessage());
    }
}
