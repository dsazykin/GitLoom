using System;
using System.Collections.Generic;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Tests.Fakes;

/// <summary>
/// Hand-rolled configurable <see cref="ILfsService"/> fake (same TI-00 no-mocking-library pattern as
/// <see cref="FakeGitService"/>). Members a test uses are backed by settable delegates; the rest
/// return benign defaults so a headless render can populate the LFS panel with canned data — no git.
/// </summary>
public sealed class FakeLfsService : ILfsService
{
    public Func<string, bool> IsAvailableImpl { get; set; } = _ => true;
    public Func<string, bool> IsEnabledForRepoImpl { get; set; } = _ => true;
    public Func<string, IReadOnlyList<string>> ListTrackedPatternsImpl { get; set; } = _ => Array.Empty<string>();
    public Func<string, IReadOnlyList<LfsFile>> ListLfsFilesImpl { get; set; } = _ => Array.Empty<LfsFile>();
    public Func<string, bool, string> PruneImpl { get; set; } = (_, _) => "prune: 0 local objects, 0 retained, done.";

    public Action<string>? InstallImpl { get; set; }
    public Action<string>? UninstallImpl { get; set; }
    public Action<string, string>? TrackImpl { get; set; }
    public Action<string, string>? UntrackImpl { get; set; }
    public Action<string>? PullImpl { get; set; }

    public bool IsAvailable(string repoPath) => IsAvailableImpl(repoPath);
    public bool IsEnabledForRepo(string repoPath) => IsEnabledForRepoImpl(repoPath);
    public void Install(string repoPath) => InstallImpl?.Invoke(repoPath);
    public void Uninstall(string repoPath) => UninstallImpl?.Invoke(repoPath);
    public void Track(string repoPath, string pattern) => TrackImpl?.Invoke(repoPath, pattern);
    public void Untrack(string repoPath, string pattern) => UntrackImpl?.Invoke(repoPath, pattern);
    public IReadOnlyList<string> ListTrackedPatterns(string repoPath) => ListTrackedPatternsImpl(repoPath);

    public IReadOnlyList<LfsFile> ListLfsFiles(string repoPath) => ListLfsFilesImpl(repoPath);

    public IReadOnlyList<string> ListFiles(string repoPath)
    {
        var paths = new List<string>();
        foreach (var f in ListLfsFilesImpl(repoPath)) paths.Add(f.Path);
        return paths;
    }

    public void Pull(string repoPath) => PullImpl?.Invoke(repoPath);
    public string Prune(string repoPath, bool dryRun) => PruneImpl(repoPath, dryRun);
}
