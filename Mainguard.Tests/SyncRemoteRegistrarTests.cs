using System;
using System.IO;
using System.Linq;
using LibGit2Sharp;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.Services;
using Mainguard.App.Shell.Services;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-06 Windows-side idempotent sync-remote registration (TI test 3/15). The registrar
/// takes the remote name/URL verbatim from the daemon response — never a hardcoded literal —
/// and is exercised here against a real <see cref="GitService"/> over a temp repo.
/// </summary>
public sealed class SyncRemoteRegistrarTests : IDisposable
{
    private readonly string _repoPath;

    public SyncRemoteRegistrarTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "gitloom-registrar-" + Guid.NewGuid().ToString("N"));
        Repository.Init(_repoPath);
    }

    [Fact]
    public void Register_RunTwice_ProducesExactlyOneRemote()
    {
        var git = new GitService();
        var registrar = new SyncRemoteRegistrar(git);
        var url = @"\\wsl.localhost\GitLoomEnv\home\u\gitloom\repos\abc.git";

        registrar.Register(_repoPath, "gitloom-vm", url);
        registrar.Register(_repoPath, "gitloom-vm", url);

        var remotes = git.GetRemotes(_repoPath);
        Assert.Single(remotes);
        Assert.Equal("gitloom-vm", remotes[0].Name);
        Assert.Equal(url, remotes[0].FetchUrl);
    }

    [Fact]
    public void Register_ChangedUrl_UpdatesInPlace()
    {
        var git = new GitService();
        var registrar = new SyncRemoteRegistrar(git);

        registrar.Register(_repoPath, "gitloom-vm", "file:///old.git");
        registrar.Register(_repoPath, "gitloom-vm", "file:///new.git");

        var remotes = git.GetRemotes(_repoPath);
        Assert.Single(remotes);
        Assert.Equal("file:///new.git", remotes[0].FetchUrl);
    }

    [Fact]
    public void Register_UsesResolvedName_NotAHardcodedLiteral()
    {
        var git = new GitService();
        var registrar = new SyncRemoteRegistrar(git);

        // A different substrate resolves a different name; the registrar honors it.
        registrar.Register(_repoPath, "gitloom-cloud", "https://sync.example/abc.git");

        Assert.Equal("gitloom-cloud", git.GetRemotes(_repoPath).Single().Name);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_repoPath))
            {
                foreach (var file in Directory.EnumerateFiles(_repoPath, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_repoPath, recursive: true);
            }
        }
        catch
        {
            // Never fail a test from cleanup.
        }
    }
}
