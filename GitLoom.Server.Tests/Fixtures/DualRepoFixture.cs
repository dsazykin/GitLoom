using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LibGit2Sharp;

namespace GitLoom.Server.Tests.Fixtures;

/// <summary>
/// TI-P2-00 §A.4.4 — a Windows-side working repo plus an "ext4-style" bare mirror
/// (a plain second directory on CI), wired the way P2-06 provisions: a bare clone the
/// agent worktrees will sync to via the <c>gitloom-vm</c> quarantine remote. Provides
/// <see cref="CaptureRefState"/> (v1 TI-19 helper, promoted here) for byte-identical
/// round-trip assertions. P2-06 fills in the worktree/quarantine flow; this fixture is
/// the substrate it stands on.
/// </summary>
public sealed class DualRepoFixture : IDisposable
{
    private readonly List<string> _owned = new();

    /// <summary>The developer-side working repository (the "Windows side").</summary>
    public string WorkRepoPath { get; }

    /// <summary>The bare mirror (the "ext4 side") registered as remote <c>gitloom-vm</c>.</summary>
    public string BareMirrorPath { get; }

    public const string QuarantineRemote = "gitloom-vm";

    public DualRepoFixture()
    {
        WorkRepoPath = NewDir("gitloom-dual-work-");
        BareMirrorPath = NewDir("gitloom-dual-bare-");

        Repository.Init(WorkRepoPath);
        using (var repo = new Repository(WorkRepoPath))
        {
            repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
            repo.Config.Set("user.email", "test@gitloom.local", ConfigurationLevel.Local);
            repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        }

        Commit("README.md", "seed\n", "seed commit");

        Repository.Init(BareMirrorPath, isBare: true);
        using (var repo = new Repository(WorkRepoPath))
        {
            var remote = repo.Network.Remotes.Add(QuarantineRemote, BareMirrorPath);
            repo.Network.Push(remote, repo.Head.CanonicalName + ":" + repo.Head.CanonicalName);
        }
    }

    /// <summary>Writes, stages and commits a file into the working repo; returns the SHA.</summary>
    public string Commit(string relPath, string content, string message)
    {
        var full = Path.Combine(WorkRepoPath, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        using var repo = new Repository(WorkRepoPath);
        Commands.Stage(repo, relPath);
        var sig = new Signature("test-user", "test@gitloom.local", DateTimeOffset.Now);
        return repo.Commit(message, sig, sig).Sha;
    }

    /// <summary>
    /// Snapshots every ref → tip SHA for a repo (v1 <c>CaptureRefState</c>, promoted).
    /// Two snapshots compare equal iff the ref graph is byte-identical.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CaptureRefState(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Refs
            .Where(r => r is DirectReference)
            .ToDictionary(r => r.CanonicalName, r => r.ResolveToDirectReference().TargetIdentifier, StringComparer.Ordinal);
    }

    private string NewDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _owned.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _owned)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(path, recursive: true);
            }
            catch
            {
                // Never fail a test from cleanup.
            }
        }
    }
}
