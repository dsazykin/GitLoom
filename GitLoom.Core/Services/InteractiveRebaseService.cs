using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Core.Services;

public class InteractiveRebaseService : IInteractiveRebaseService
{
    public IReadOnlyList<RebaseTodoItem> GetRebasePlan(string repoPath, string baseSha)
    {
        using var repo = new Repository(repoPath);
        var baseCommit = repo.Lookup<Commit>(baseSha);
        if (baseCommit == null) throw new GitOperationException($"Base commit {baseSha} not found.");

        var filter = new CommitFilter
        {
            IncludeReachableFrom = repo.Head.Tip,
            ExcludeReachableFrom = baseCommit,
            SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse
        };

        var commits = repo.Commits.QueryBy(filter).ToList();
        
        var plan = new List<RebaseTodoItem>();
        foreach (var c in commits)
        {
            plan.Add(new RebaseTodoItem
            {
                Sha = c.Sha,
                Message = c.MessageShort,
                Action = RebaseAction.Pick
            });
        }

        return plan;
    }

    public void StartInteractiveRebase(string repoPath, string baseSha, IReadOnlyList<RebaseTodoItem> plan, CancellationToken ct = default)
    {
        using (var repo = new Repository(repoPath))
        {
            if (repo.RetrieveStatus().IsDirty)
            {
                throw new GitOperationException("Working tree is dirty. Stash or discard your changes before rebasing.");
            }

            if (Directory.Exists(Path.Combine(repoPath, ".git", "rebase-merge")) ||
                Directory.Exists(Path.Combine(repoPath, ".git", "rebase-apply")))
            {
                throw new GitOperationException("A rebase is already in progress.");
            }
        }

        if (plan.Count > 0 && (plan[0].Action == RebaseAction.Squash || plan[0].Action == RebaseAction.Fixup))
        {
            throw new GitOperationException("The first commit in a rebase plan cannot be Squash or Fixup.");
        }

        var msgDir = Path.Combine(Path.GetTempPath(), "gitloom-rebase-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(msgDir);
        
        var todoLines = new List<string>();
        int msgCounter = 0;

        foreach (var item in plan)
        {
            if (item.Action == RebaseAction.Drop) continue;

            var actionStr = item.Action.ToString().ToLowerInvariant();
            var msgSafe = item.Message.Replace('\n', ' ');
            todoLines.Add($"{actionStr} {item.Sha} {msgSafe}");

            if (item.Action == RebaseAction.Reword || item.Action == RebaseAction.Squash)
            {
                var newMsg = item.NewMessage ?? item.Message;
                File.WriteAllText(Path.Combine(msgDir, $"{msgCounter:D4}.txt"), newMsg);
                msgCounter++;
            }
        }

        var todoPath = Path.Combine(Path.GetTempPath(), "gitloom-todo-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(todoPath, todoLines);

        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exePath)) exePath = "GitLoom.App.exe";

        var env = new Dictionary<string, string>
        {
            ["GIT_SEQUENCE_EDITOR"] = $"\"{exePath}\" --rebase-editor \"{todoPath}\"",
            ["GIT_EDITOR"] = $"\"{exePath}\" --rebase-msg \"{msgDir}\""
        };

        var (code, outStr, errStr) = GitService.RunGit(repoPath, env, ct, "rebase", "-i", baseSha);

        try { File.Delete(todoPath); } catch { }
        try { Directory.Delete(msgDir, true); } catch { }

        if (code != 0)
        {
            using var repo = new Repository(repoPath);
            if (repo.Index.Conflicts.Any())
            {
                throw new MergeConflictException("Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then click 'Continue Rebase'.");
            }
            
            var stoppedShaPath = Path.Combine(repoPath, ".git", "rebase-merge", "stopped-sha");
            if (File.Exists(stoppedShaPath))
            {
                var sha = File.ReadAllText(stoppedShaPath).Trim();
                throw new GitOperationException($"Rebase paused at {sha} for editing. Amend your changes, then continue.");
            }

            throw new GitOperationException($"Rebase failed: {errStr}");
        }
    }

    public string? GetRebaseProgress(string repoPath)
    {
        var msgnumPath = Path.Combine(repoPath, ".git", "rebase-merge", "msgnum");
        var endPath = Path.Combine(repoPath, ".git", "rebase-merge", "end");

        if (File.Exists(msgnumPath) && File.Exists(endPath))
        {
            var msgnum = File.ReadAllText(msgnumPath).Trim();
            var end = File.ReadAllText(endPath).Trim();
            return $"step {msgnum}/{end}";
        }

        return null;
    }
}
