using System;
using System.Collections.Generic;
using System.IO;
using LibGit2Sharp;

namespace GitLoom.Tests.Fixtures;

/// <summary>
/// Disposable temp Git repository with builder helpers for integration tests
/// (task T-01 of the Master Implementation Document). Every helper opens and
/// disposes its own <see cref="Repository"/> handle — the fixture never caches
/// one — mirroring the ExecuteWithRepo handle discipline the app itself uses.
/// </summary>
public sealed class TempRepoFixture : IDisposable
{
    private readonly List<string> _ownedPaths = new();

    public string RepoPath { get; }

    public TempRepoFixture()
    {
        RepoPath = NewTempPath("gitloom-test-");
        Repository.Init(RepoPath);
        using var repo = new Repository(RepoPath);
        repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
        repo.Config.Set("user.email", "test@gitloom.local", ConfigurationLevel.Local);
        // Git for Windows ships system-level core.autocrlf=true, which rewrites
        // committed LF content to CRLF on checkout and breaks byte-exact content
        // assertions. Pin it off locally so tests are deterministic across OSes.
        repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
    }

    public void WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(RepoPath, relativePath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
    }

    /// <summary>Writes, stages and commits a file. Returns the commit SHA.</summary>
    public string CommitFile(string relativePath, string content, string message)
        => CommitFile(relativePath, content, message, "test-user", "test@gitloom.local", DateTimeOffset.Now);

    /// <summary>Author/date-controlled overload for filter and analytics tests.</summary>
    public string CommitFile(string relativePath, string content, string message,
        string authorName, string authorEmail, DateTimeOffset when)
    {
        WriteFile(relativePath, content);
        using var repo = new Repository(RepoPath);
        Commands.Stage(repo, relativePath);
        var sig = new Signature(authorName, authorEmail, when);
        return repo.Commit(message, sig, sig).Sha;
    }

    /// <summary>Creates a branch at HEAD without checking it out. Returns the name.</summary>
    public string CreateBranch(string name)
    {
        using var repo = new Repository(RepoPath);
        repo.CreateBranch(name);
        return name;
    }

    public void Checkout(string name)
    {
        using var repo = new Repository(RepoPath);
        var branch = repo.Branches[name]
            ?? throw new InvalidOperationException($"Fixture: branch '{name}' not found.");
        Commands.Checkout(repo, branch);
    }

    /// <summary>
    /// Creates two branches ("ours"/"theirs") with conflicting edits to the same
    /// line of <paramref name="relativePath"/>. Seeds a base commit for the file
    /// if it is not committed yet. Leaves HEAD on the "ours" branch.
    /// </summary>
    public (string Ours, string Theirs) CreateConflict(string relativePath, string oursContent, string theirsContent)
    {
        bool needsSeed;
        using (var repo = new Repository(RepoPath))
        {
            needsSeed = repo.Head.Tip?[relativePath] == null;
        }
        if (needsSeed)
        {
            CommitFile(relativePath, "base\n", $"seed {relativePath}");
        }

        const string ours = "ours", theirs = "theirs";
        CreateBranch(theirs);
        CreateBranch(ours);
        Checkout(theirs);
        CommitFile(relativePath, theirsContent, "theirs change");
        Checkout(ours);
        CommitFile(relativePath, oursContent, "ours change");
        return (ours, theirs);
    }

    /// <summary>
    /// Inits a bare repo, registers it as remote "origin", and pushes HEAD if
    /// the repo has commits. Enables push/pull/fetch tests with zero network.
    /// Returns the bare repo path.
    /// </summary>
    public string AddBareRemote()
    {
        var barePath = NewTempPath("gitloom-bare-");
        Repository.Init(barePath, isBare: true);
        using var repo = new Repository(RepoPath);
        var remote = repo.Network.Remotes["origin"] ?? repo.Network.Remotes.Add("origin", barePath);
        if (repo.Head.Tip != null)
        {
            repo.Network.Push(remote, $"{repo.Head.CanonicalName}:{repo.Head.CanonicalName}");
        }
        return barePath;
    }

    /// <summary>Clones the fixture repo (with a local test identity set) and returns the clone path.</summary>
    public string ClonePath()
    {
        var clonePath = NewTempPath("gitloom-clone-");
        Repository.Clone(RepoPath, clonePath);
        using var repo = new Repository(clonePath);
        repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
        repo.Config.Set("user.email", "test@gitloom.local", ConfigurationLevel.Local);
        repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        // Clone() already checked out the working tree under whatever autocrlf was
        // in effect then (Git for Windows defaults to true → CRLF on disk). With
        // autocrlf now pinned off, re-materialize the tree from HEAD so files match
        // their LF blobs; otherwise later git ops see them as locally modified.
        // libgit2's Reset leaves the CRLF bytes in place here, so use the git CLI.
        RunGit(clonePath, "-c", "core.autocrlf=false", "reset", "--hard", "HEAD");
        return clonePath;
    }

    public void Dispose()
    {
        foreach (var path in _ownedPaths)
        {
            try
            {
                ForceDelete(path);
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Fixture: failed to start git.");
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"Fixture: git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
    }

    private string NewTempPath(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        _ownedPaths.Add(path);
        return path;
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;

        // Git pack/object files are read-only; clear attributes before deleting
        // (required on Windows, harmless elsewhere).
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
        Directory.Delete(path, recursive: true);
    }
}
