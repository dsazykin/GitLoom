using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Mainguard.Agents.Services;
using Mainguard.Git;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
namespace Mainguard.Agents.Agents;

/// <summary>
/// P2-06 daemon service (no UI dependency). Manages per-agent worktrees off a repo's bare
/// mirror: create <c>agent/&lt;id&gt;</c> off the mirror's default branch, remove, and prune.
/// Every worktree is <b>quarantined</b> — its sole configured remote is the daemon-owned bare
/// mirror (§3.4), so an agent's <c>git push</c> can only land in the mirror, never the user's
/// real remote and never with credentials it does not have.
/// </summary>
public interface IAgentWorktreeManager
{
    /// <summary>Create the worktree for an agent (branch <c>agent/&lt;id&gt;</c> off the mirror's default branch). Returns its path.</summary>
    string CreateAgentWorktree(string repoHash, string agentId);

    /// <summary>Remove an agent's worktree; <paramref name="force"/> discards a dirty tree, otherwise a dirty tree is refused (typed).</summary>
    void RemoveAgentWorktree(string repoHash, string agentId, bool force);

    /// <summary>Prune stale worktree metadata from the bare mirror.</summary>
    void Prune(string repoHash);

    /// <summary>List the mirror's worktrees via the porcelain parser (drives the ListWorktrees RPC).</summary>
    IReadOnlyList<WorktreeItem> List(string repoHash);
}

/// <inheritdoc cref="IAgentWorktreeManager"/>
public sealed class WorktreeManager : IAgentWorktreeManager
{
    private readonly string _vmRoot;
    private readonly Func<string, (int ExitCode, string Output)> _pnpmRunner;
    private readonly Action<string>? _warningSink;

    /// <param name="vmRoot">The VM base directory (shared with the provisioner). Injected for tests.</param>
    /// <param name="pnpmRunner">
    /// Runs <c>pnpm install</c> in a worktree. Injected so tests can assert the command was
    /// <i>issued</i> (or simulate a failure) without running real pnpm; defaults to a real spawn.
    /// This is the ONE process launch in <c>Mainguard.Agents/Agents</c> — all git goes through the
    /// shared <see cref="GitServices.RunGit"/> primitive.
    /// </param>
    /// <param name="warningSink">Receives non-fatal warnings (e.g. a failed pnpm install).</param>
    public WorktreeManager(
        string? vmRoot = null,
        Func<string, (int ExitCode, string Output)>? pnpmRunner = null,
        Action<string>? warningSink = null)
    {
        _vmRoot = vmRoot ?? DefaultVmRoot();
        _pnpmRunner = pnpmRunner ?? RealPnpmInstall;
        _warningSink = warningSink;
    }

    public string CreateAgentWorktree(string repoHash, string agentId)
    {
        var barePath = BareRepoPathFor(repoHash);
        if (!Directory.Exists(barePath))
        {
            throw new RepoProvisioningException($"No provisioned mirror for repo '{repoHash}'; provision it first.");
        }

        var branch = BranchFor(agentId);
        var worktreePath = WorktreePathFor(repoHash, agentId);

        // Refuse (typed) BEFORE any mutation if the branch or the path already exists (edge row 3):
        // leave no residue.
        if (BranchExists(barePath, branch))
        {
            throw new AgentWorktreeConflictException($"Branch '{branch}' already exists for repo '{repoHash}'.");
        }

        if (Directory.Exists(worktreePath))
        {
            throw new AgentWorktreeConflictException($"Worktree path already exists for agent '{agentId}'.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        var baseBranch = DefaultBranch(barePath);
        AgentGitCommand.Run(barePath, "worktree", "add", "-b", branch, worktreePath, baseBranch);

        // Quarantine remote (§3.4): the worktree's remotes MUST be exactly {origin -> bare mirror}.
        // Remove any inherited origin first, then point origin at the local bare path only —
        // never the user's real remote, never credentials.
        AgentGitCommand.TryRun(worktreePath, out _, "remote", "remove", "origin");
        AgentGitCommand.Run(worktreePath, "remote", "add", "origin", barePath);

        // pnpm hook (§3.3): only when a lockfile is present, and non-fatal — a failure surfaces
        // a warning but the worktree is still returned.
        MaybeRunPnpm(worktreePath, agentId);

        return worktreePath;
    }

    public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
    {
        var barePath = BareRepoPathFor(repoHash);
        var worktreePath = WorktreePathFor(repoHash, agentId);
        var branch = BranchFor(agentId);

        if (Directory.Exists(worktreePath))
        {
            if (!force && IsDirty(worktreePath))
            {
                throw new AgentWorktreeConflictException(
                    $"Worktree for agent '{agentId}' has uncommitted changes; pass force to discard them.");
            }

            if (force)
            {
                AgentGitCommand.Run(barePath, "worktree", "remove", "--force", worktreePath);
            }
            else
            {
                AgentGitCommand.Run(barePath, "worktree", "remove", worktreePath);
            }
        }

        // Prune any dangling metadata and delete the agent branch so no residue survives either way.
        AgentGitCommand.TryRun(barePath, out _, "worktree", "prune");
        AgentGitCommand.TryRun(barePath, out _, "branch", "-D", branch);
    }

    public void Prune(string repoHash)
    {
        var barePath = BareRepoPathFor(repoHash);
        AgentGitCommand.Run(barePath, "worktree", "prune");
    }

    public IReadOnlyList<WorktreeItem> List(string repoHash)
    {
        var barePath = BareRepoPathFor(repoHash);
        var porcelain = AgentGitCommand.Run(barePath, "worktree", "list", "--porcelain");
        return WorktreePorcelainParser.Parse(porcelain);
    }

    /// <summary>The bare-mirror path for a hash (shared layout with the provisioner).</summary>
    public string BareRepoPathFor(string repoHash) => Path.Combine(_vmRoot, "repos", repoHash + ".git");

    /// <summary>The worktree path for an agent: <c>&lt;vmRoot&gt;/worktrees/&lt;hash&gt;/&lt;agentId&gt;</c>.</summary>
    public string WorktreePathFor(string repoHash, string agentId)
        => Path.Combine(_vmRoot, "worktrees", repoHash, agentId);

    private static string BranchFor(string agentId) => "agent/" + agentId;

    private void MaybeRunPnpm(string worktreePath, string agentId)
    {
        if (!File.Exists(Path.Combine(worktreePath, "pnpm-lock.yaml")))
        {
            return;
        }

        try
        {
            var (exitCode, output) = _pnpmRunner(worktreePath);
            if (exitCode != 0)
            {
                _warningSink?.Invoke(
                    $"pnpm install failed for agent '{agentId}' (exit {exitCode}); the worktree was still created. {output.Trim()}");
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: agents can still run without dependencies installed.
            _warningSink?.Invoke($"pnpm install could not run for agent '{agentId}': {ex.Message}");
        }
    }

    private static bool BranchExists(string barePath, string branch)
        => AgentGitCommand.TryRun(barePath, out _, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch) == 0;

    private static bool IsDirty(string worktreePath)
        => AgentGitCommand.Run(worktreePath, "status", "--porcelain").Trim().Length > 0;

    private static string DefaultBranch(string barePath)
    {
        // The mirror's HEAD points at the source's default branch (main/master); base the worktree
        // off whatever that is rather than assuming a literal name.
        if (AgentGitCommand.TryRun(barePath, out var output, "symbolic-ref", "--short", "HEAD") == 0)
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    private static (int ExitCode, string Output) RealPnpmInstall(string worktreePath)
    {
        // The single, injectable, real process launch in Mainguard.Agents/Agents. Git never spawns here.
        var psi = new ProcessStartInfo
        {
            FileName = "pnpm",
            WorkingDirectory = worktreePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("install");

        using var process = Process.Start(psi)
            ?? throw new RepoProvisioningException("Failed to launch pnpm.");
        // Drain BOTH pipes concurrently: pnpm writes progress to stderr, and reading stdout to end
        // first deadlocks once stderr fills its ~64KB pipe buffer (the audit-flagged wsl-runner bug
        // class) — which would hang worktree creation inside the daemon.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return (process.ExitCode, combined);
    }

    // GitLoomPaths.HomeDirectory(), not the old `?? "."` fallback: a relative VM root silently
    // resolving against the daemon's CWD is exactly the class of bug that crash-looped gitloomd.
    // An unresolvable home now fails loudly with the systemd remedy named.
    private static string DefaultVmRoot()
        => Path.Combine(GitLoomPaths.HomeDirectory(), "gitloom");
}
