using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

// TI-16: submodules over local fixtures (no network). A superproject gets a file-based submodule
// added; every fixture arrangement that touches the file:// transport enables
// `protocol.file.allow` — but ONLY in the local git config of the test repos, never through the
// production code path (GitServices passes no such flag). The production UpdateSubmodules then
// inherits the config, exercising the real code without a rejection-trigger `-c` in Core.
[Trait("Category", "RequiresGitCli")]
public class GitServiceSubmoduleTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly List<string> _ownedPaths = new();

    public void Dispose()
    {
        foreach (var p in _ownedPaths)
        {
            try { ForceDelete(p); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void GetSubmodules_NoSubmodules_ShouldReturnEmpty()
    {
        // Missing .gitmodules edge case: a plain repo reports no submodules, no throw.
        using var super = new TempRepoFixture();
        super.CommitFile("a.txt", "hello\n", "seed");

        Assert.Empty(_git.GetSubmodules(super.RepoPath));
    }

    [Fact]
    public void Submodules_FreshClone_ShouldReportUninitialized_ThenUpToDateAfterInit()
    {
        var (superPath, _) = BuildSuperWithSubmodule("lib");

        // Reproduce a fresh-clone "registered but not checked out" state via deinit. This keeps the
        // cached submodule gitdir (.git/modules/lib), so the *production* UpdateSubmodules re-checks
        // it out with no transport — a genuine file:// re-clone would require protocol.file.allow,
        // which production must never set (rejection trigger). Real users clone submodules over
        // https/ssh where the file-protocol guard does not apply, so this is faithful to the intent.
        Run(superPath, "submodule", "deinit", "-f", "--", "lib");

        var before = Assert.Single(_git.GetSubmodules(superPath));
        Assert.Equal("lib", before.Path);
        Assert.False(string.IsNullOrEmpty(before.Url));
        Assert.Equal(SubmoduleState.Uninitialized, before.Status);

        // Production init path — no protocol flag, checks out from the cached gitdir.
        _git.UpdateSubmodules(superPath);

        var after = Assert.Single(_git.GetSubmodules(superPath));
        Assert.Equal(SubmoduleState.UpToDate, after.Status);
        Assert.False(string.IsNullOrEmpty(after.HeadSha));
    }

    [Fact]
    public void Submodule_InnerCommit_ShouldFlagSuperprojectModified()
    {
        var (superPath, _) = BuildSuperWithSubmodule("lib");
        var subWorkdir = Path.Combine(superPath, "lib");

        // The submodule is checked out at the recorded commit → UpToDate.
        Assert.Equal(SubmoduleState.UpToDate, Assert.Single(_git.GetSubmodules(superPath)).Status);

        // Advance the submodule's own HEAD past what the superproject records.
        SetIdentity(subWorkdir);
        File.WriteAllText(Path.Combine(subWorkdir, "extra.txt"), "more\n");
        Run(subWorkdir, "add", "extra.txt");
        Run(subWorkdir, "commit", "-m", "inner commit");

        Assert.Equal(SubmoduleState.Modified, Assert.Single(_git.GetSubmodules(superPath)).Status);
    }

    [Fact]
    public void Submodule_UncommittedInnerChange_ShouldFlagDirty()
    {
        var (superPath, _) = BuildSuperWithSubmodule("lib");
        var subWorkdir = Path.Combine(superPath, "lib");

        // Untracked content inside the submodule, still at the recorded commit → Dirty.
        File.WriteAllText(Path.Combine(subWorkdir, "scratch.txt"), "wip\n");

        Assert.Equal(SubmoduleState.Dirty, Assert.Single(_git.GetSubmodules(superPath)).Status);
    }

    [Fact]
    public void GetSubmodules_MultipleEntries_ShouldListAll_SortedByPath()
    {
        // Nested .gitmodules with multiple entries, one path containing a space.
        var super = NewRepoWithFileProtocol("gitloom-super-");
        super.CommitFile("a.txt", "hello\n", "seed");
        var srcA = BuildSubmoduleSource("alpha\n");
        var srcB = BuildSubmoduleSource("beta\n");

        AddSubmodule(super.RepoPath, srcB, "vendor/zeta");
        AddSubmodule(super.RepoPath, srcA, "vendor/lib with space");
        Run(super.RepoPath, "commit", "-m", "add two submodules");

        var items = _git.GetSubmodules(super.RepoPath);

        Assert.Equal(2, items.Count);
        Assert.Equal("vendor/lib with space", items[0].Path); // ordinal-sorted
        Assert.Equal("vendor/zeta", items[1].Path);
        Assert.All(items, i => Assert.False(string.IsNullOrEmpty(i.Url)));
    }

    [Fact]
    public void GetSubmodules_PathWithSpaces_ShouldRoundTrip()
    {
        var super = NewRepoWithFileProtocol("gitloom-super-");
        super.CommitFile("a.txt", "hello\n", "seed");
        var src = BuildSubmoduleSource("spaced\n");

        AddSubmodule(super.RepoPath, src, "my libs/lib one");
        Run(super.RepoPath, "commit", "-m", "add spaced submodule");

        var item = Assert.Single(_git.GetSubmodules(super.RepoPath));
        Assert.Equal("my libs/lib one", item.Path);
        Assert.Equal(SubmoduleState.UpToDate, item.Status);
    }

    [Fact]
    public void SyncSubmodules_ShouldSucceed_AfterUrlEdit()
    {
        var (superPath, srcPath) = BuildSuperWithSubmodule("lib");

        // Point .gitmodules at a moved copy of the source, then sync — a no-op-safe mutation.
        var moved = srcPath + "-moved";
        CopyDir(srcPath, moved);
        _ownedPaths.Add(moved);
        Run(superPath, "config", "-f", ".gitmodules", "submodule.lib.url", moved);

        var ex = Record.Exception(() => _git.SyncSubmodules(superPath));
        Assert.Null(ex);
    }

    // ---- fixture builders -------------------------------------------------------------------

    // Builds a superproject with a single file-based submodule at <path>, committed. Returns the
    // superproject working dir and the submodule source repo path.
    private (string SuperPath, string SrcPath) BuildSuperWithSubmodule(string path)
    {
        var super = NewRepoWithFileProtocol("gitloom-super-");
        super.CommitFile("a.txt", "hello\n", "seed");
        var src = BuildSubmoduleSource("v1\n");
        AddSubmodule(super.RepoPath, src, path);
        Run(super.RepoPath, "commit", "-m", $"add submodule {path}");
        return (super.RepoPath, src);
    }

    // A standalone repo (with one commit) used as a submodule's upstream, over file:// transport.
    private string BuildSubmoduleSource(string content)
    {
        var fx = new TempRepoFixture();
        fx.CommitFile("lib.txt", content, "sub init");
        // TempRepoFixture is disposable and owns its own cleanup; keep it alive via its path only.
        _ownedPaths.Add(fx.RepoPath);
        return fx.RepoPath;
    }

    private TempRepoFixture NewRepoWithFileProtocol(string _)
    {
        var fx = new TempRepoFixture();
        // Arrangement-only: allow the file:// transport for submodule add/update in THIS repo's
        // local config. Production code never sets protocol.file.allow (rejection trigger).
        Run(fx.RepoPath, "config", "protocol.file.allow", "always");
        _ownedPaths.Add(fx.RepoPath);
        return fx;
    }

    private void AddSubmodule(string superPath, string srcPath, string path)
    {
        // -c on the arrangement command is test-only; the local config set above also covers update.
        Run(superPath, "-c", "protocol.file.allow=always", "submodule", "add", srcPath, path);
    }

    private static void SetIdentity(string repoPath)
    {
        Run(repoPath, "config", "user.name", "test-user");
        Run(repoPath, "config", "user.email", "test@gitloom.local");
        Run(repoPath, "config", "core.autocrlf", "false");
    }

    private static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), overwrite: true);
    }

    private static (int Code, string Out, string Err) Run(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to start git");
        var o = p.StandardOutput.ReadToEnd();
        var e = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && args.FirstOrDefault() != "config")
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {e}");
        return (p.ExitCode, o, e);
    }

    private static void ForceDelete(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
