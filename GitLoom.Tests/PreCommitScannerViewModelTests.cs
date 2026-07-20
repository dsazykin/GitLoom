using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using Mainguard.Agents;
using Mainguard.Git.Models;
using Mainguard.Git.Safety;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

/// <summary>
/// TI-30 — VM gating for the pre-commit scanner. The <see cref="PreCommitFindingsViewModel"/> groups
/// findings and drives the "Commit anyway" override; the <see cref="StagingPanelViewModel"/> gates a
/// commit on any blocker and honors the enable/disable toggle. Uses a real journal-free GitService
/// over a fixture repo so a real commit either lands or does not.
/// </summary>
[Trait("Category", "RequiresGitCli")]
public sealed class PreCommitScannerViewModelTests : IDisposable
{
    private const string PlantedAwsKey = "AKIAIOSFODNN7EXAMPLE";

    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    private int CommitCount()
    {
        using var repo = new Repository(_fx.RepoPath);
        return repo.Head.Tip == null ? 0 : repo.Commits.Count();
    }

    // ---- PreCommitFindingsViewModel display ----

    [Fact]
    public void SetFindings_ShouldGroupBySeverity_AndReportBlockers()
    {
        var vm = new PreCommitFindingsViewModel(new PreCommitScanner(_git), _fx.RepoPath);
        vm.SetFindings(new[]
        {
            new PreCommitFinding { Kind = FindingKind.Secret, Severity = FindingSeverity.Blocker, Path = "a", Line = 1, Message = "m", Rule = "r" },
            new PreCommitFinding { Kind = FindingKind.LargeFile, Severity = FindingSeverity.Warning, Path = "b", Message = "m", Rule = "large-file" },
            new PreCommitFinding { Kind = FindingKind.ManyFiles, Severity = FindingSeverity.Info, Path = "", Message = "m", Rule = "x" },
        });

        Assert.True(vm.HasRun);
        Assert.True(vm.HasFindings);
        Assert.True(vm.HasBlockers);
        Assert.False(vm.IsAllClear);
        Assert.Equal(3, vm.Groups.Count);                       // one group per severity
        Assert.True(vm.Groups[0].IsBlocker);                    // blocker group first
        Assert.Equal(1, vm.BlockerCount);
        Assert.Equal(1, vm.WarningCount);
        Assert.Equal(1, vm.InfoCount);
    }

    [Fact]
    public void SetFindings_Empty_ShouldBeAllClear()
    {
        var vm = new PreCommitFindingsViewModel(new PreCommitScanner(_git), _fx.RepoPath);
        vm.SetFindings(Array.Empty<PreCommitFinding>());

        Assert.True(vm.HasRun);
        Assert.False(vm.HasFindings);
        Assert.False(vm.HasBlockers);
        Assert.True(vm.IsAllClear);
    }

    [Fact]
    public async Task CommitAnyway_ShouldRaiseCommitConfirmed_AndReset()
    {
        var vm = new PreCommitFindingsViewModel(new PreCommitScanner(_git), _fx.RepoPath);
        vm.SetFindings(new[]
        {
            new PreCommitFinding { Kind = FindingKind.Secret, Severity = FindingSeverity.Blocker, Path = "a", Message = "m", Rule = "r" },
        });
        vm.AwaitingOverride = true;
        bool confirmed = false;
        vm.CommitConfirmed += () => { confirmed = true; return Task.CompletedTask; };

        await vm.CommitAnywayCommand.ExecuteAsync(null);

        Assert.True(confirmed);
        Assert.False(vm.AwaitingOverride);
        Assert.False(vm.HasFindings);   // reset back to pre-scan state
    }

    [Fact]
    public void AutoScanEnabled_ShouldPersistToSettings()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"gitloom-prefs-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new SettingsService(tempFile);
            var vm = new PreCommitFindingsViewModel(new PreCommitScanner(_git), _fx.RepoPath,
                preferences: () => settings.Current, settings: settings);

            Assert.True(vm.AutoScanEnabled);   // default on
            vm.AutoScanEnabled = false;
            Assert.False(settings.Current.PreCommitScanEnabled);
            Assert.False(vm.AutoScanEnabled);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    // ---- StagingPanelViewModel commit gating ----

    private StagingPanelViewModel StagingWithPrefs(UserPreferences prefs)
    {
        var vm = new StagingPanelViewModel(_git, _fx.RepoPath, onCommitAction: () => { },
            showNotification: null,
            scanner: new PreCommitScanner(_git),
            preferences: () => prefs,
            settings: null);
        vm.UpdateStatus(_git.GetRepositoryStatus(_fx.RepoPath));
        return vm;
    }

    [Fact]
    public async Task Commit_ShouldBeBlocked_ByASecret_ThenCommitAnywayOverrides()
    {
        _fx.WriteFile("creds.env", $"AWS_ACCESS_KEY_ID={PlantedAwsKey}\n");
        _git.StageFile(_fx.RepoPath, "creds.env");

        var vm = StagingWithPrefs(new UserPreferences { PreCommitScanEnabled = true });
        vm.CommitMessage = "add creds";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.True(vm.PreCommitFindings.AwaitingOverride);   // blocker banner shown
        Assert.True(vm.PreCommitFindings.HasBlockers);
        Assert.Equal(0, CommitCount());                       // nothing committed yet

        await vm.PreCommitFindings.CommitAnywayCommand.ExecuteAsync(null);

        Assert.Equal(1, CommitCount());                       // explicit override committed
        Assert.False(vm.PreCommitFindings.AwaitingOverride);
    }

    [Fact]
    public async Task Commit_ShouldProceed_WhenScanDisabled_EvenWithASecret()
    {
        _fx.WriteFile("creds.env", $"AWS_ACCESS_KEY_ID={PlantedAwsKey}\n");
        _git.StageFile(_fx.RepoPath, "creds.env");

        var vm = StagingWithPrefs(new UserPreferences { PreCommitScanEnabled = false });
        vm.CommitMessage = "add creds";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.PreCommitFindings.AwaitingOverride);
        Assert.Equal(1, CommitCount());                       // committed without scanning
    }

    [Fact]
    public async Task Commit_ShouldProceed_OnACleanStage_WithScanEnabled()
    {
        _fx.WriteFile("notes.txt", "just some ordinary notes\n");
        _git.StageFile(_fx.RepoPath, "notes.txt");

        var vm = StagingWithPrefs(new UserPreferences { PreCommitScanEnabled = true });
        vm.CommitMessage = "add notes";

        await vm.CommitCommand.ExecuteAsync(null);

        Assert.False(vm.PreCommitFindings.AwaitingOverride);
        Assert.True(vm.PreCommitFindings.IsAllClear);         // scan ran, nothing found
        Assert.Equal(1, CommitCount());
    }
}
