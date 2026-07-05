using System;
using System.IO;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Core.Services;

public class GitService : IGitService
{
    public bool IsGitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.
                Exists(path))
        {
            return false;
        }

        try
        {
            return Repository.IsValid(path);
        }
        catch
        {
            return false;
        }
    }

    public void ExecuteWithRepo(string path, Action<Repository>
        action)
    {
        if (!IsGitRepository(path))
        {
            throw new ArgumentException("Path is not a valid Git repository.", nameof(path));
        }

        using var repo = new Repository(path);
        action(repo);
    }

    public T ExecuteWithRepo<T>(string path, Func<Repository, T> func)
    {
        if (!IsGitRepository(path))
        {
            throw new ArgumentException("Path is not a valid Git repository.", nameof(path));
        }

        using var repo = new Repository(path);
        return func(repo);
    }

    public List<GitFileStatus> GetRepositoryStatus(string path)
    {
        return ExecuteWithRepo(path, repo =>
        {
            var results = new List<GitFileStatus>();

            // Query the repository status (includes untracked files)
            var options = new StatusOptions { IncludeUntracked = true };
            var repoStatus = repo.RetrieveStatus(options);

            foreach (var item in repoStatus)
            {
                // We ignore Ignored files (like bin/ obj/ node_modules/) to save performance
                if (item.State == FileStatus.Ignored) continue;

                results.Add(new GitFileStatus
                {
                    FilePath = item.FilePath,
                    State = item.State
                });
            }

            return results;
        });
    }

    public void StageFile(string repoPath, string filePath)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            Commands.Stage(repo, filePath);
        });
    }

    public void UnstageFile(string repoPath, string filePath)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            Commands.Unstage(repo, filePath);
        });
    }

    public void StageFiles(string repoPath, IEnumerable<string> filePaths)
    {
        ExecuteWithRepo(repoPath, repo => Commands.Stage(repo, filePaths));
    }

    public void UnstageFiles(string repoPath, IEnumerable<string> filePaths)
    {
        ExecuteWithRepo(repoPath, repo => Commands.Unstage(repo, filePaths));
    }

    public void DiscardChanges(string repoPath, IEnumerable<string> filePaths)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var paths = filePaths.ToList();
            var status = repo.RetrieveStatus(new StatusOptions { PathSpec = paths.ToArray() });
            var trackedToCheckout = new List<string>();
            var newToRemove = new List<string>();

            foreach (var path in paths)
            {
                var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);

                // NEVER touch a directory. Discard operates on the individual file
                // paths surfaced in the status list; if a path resolves to a
                // directory, skip it rather than recursively wiping an untracked
                // tree full of the user's work (a data-loss trap).
                if (System.IO.Directory.Exists(fullPath))
                {
                    continue;
                }

                var entry = status[path];
                bool isNew = entry != null &&
                    (entry.State.HasFlag(FileStatus.NewInWorkdir) || entry.State.HasFlag(FileStatus.NewInIndex));

                if (isNew)
                {
                    newToRemove.Add(path);
                }
                else
                {
                    trackedToCheckout.Add(path);
                }
            }

            foreach (var path in newToRemove)
            {
                // Unstage staged-new files first so they fully disappear from the
                // index instead of lingering as a phantom "deleted" entry.
                var entry = status[path];
                if (entry != null && entry.State.HasFlag(FileStatus.NewInIndex))
                {
                    Commands.Unstage(repo, path);
                }

                var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);
                if (System.IO.File.Exists(fullPath))
                {
                    SafeDeleteFile(fullPath);
                }
            }

            if (trackedToCheckout.Count > 0)
            {
                repo.CheckoutPaths(repo.Head.FriendlyName, trackedToCheckout.ToArray(), new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            }
        });
    }

    // --- Partial (hunk / line) staging -------------------------------------
    // Whole-file staging (StageFile) is not enough for crafting atomic commits.
    // These apply a caller-selected subset of a unified-diff patch to the index
    // or working tree via `git apply`, which is how serious clients implement
    // reliable partial staging. The UI (Category 2.13) builds the patch subset.

    public void StageHunk(string repoPath, string patch)
    {
        // Apply the selected hunk(s) to the index only.
        ApplyPatch(repoPath, patch, "--cached");
    }

    public void UnstageHunk(string repoPath, string patch)
    {
        // Reverse the selected hunk(s) out of the index.
        ApplyPatch(repoPath, patch, "--cached", "--reverse");
    }

    public void DiscardHunk(string repoPath, string patch)
    {
        // Reverse the selected hunk(s) out of the working tree.
        ApplyPatch(repoPath, patch, "--reverse");
    }

    private static void ApplyPatch(string repoPath, string patch, params string[] applyArgs)
    {
        if (string.IsNullOrEmpty(patch)) return;

        // git apply requires the patch to terminate with a newline.
        if (!patch.EndsWith("\n")) patch += "\n";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("apply");
        foreach (var a in applyArgs) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-"); // read the patch from stdin (never a temp file / argv)

        // git apply never needs a prompt; keep it non-interactive so it can't hang.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi)
                      ?? throw new GitOperationException(
                          "Failed to launch git. Is Git installed and on the PATH?");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitOperationException(
                "Failed to launch git. Is Git installed and on the PATH?", ex);
        }

        using (process)
        {
            // Drain both output pipes concurrently BEFORE the blocking stdin write:
            // a large patch could otherwise deadlock, each side blocked on a full
            // pipe (we on stdin, git on an unread stdout/stderr).
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            process.StandardInput.Write(patch);
            process.StandardInput.Close();

            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                var err = stderrTask.Result;
                throw new GitOperationException(string.IsNullOrWhiteSpace(err)
                    ? $"git apply failed with exit code {process.ExitCode}."
                    : err);
            }
        }
    }

    /// <summary>
    /// Removes a single working-tree file. On Windows the file is sent to the
    /// Recycle Bin so a mis-clicked discard is recoverable; on other platforms
    /// (no standard trash API) it falls back to a hard delete.
    /// </summary>
    private static void SafeDeleteFile(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                fullPath,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            System.IO.File.Delete(fullPath);
        }
    }

    public string GetFileDiff(string repoPath, string filePath, bool isStaged)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            Patch patch;
            if (isStaged)
            {
                // Compare HEAD against the Staging Area (Index)
                // If the repo is brand new and has no commits, Head.Tip will be null, which LibGit2Sharp handles gracefully
                Tree? headTree = repo.Head.Tip?.Tree;
                patch = repo.Diff.Compare<Patch>(headTree, DiffTargets.Index, new[] { filePath });
            }
            else
            {
                // Compare the Staging Area (Index) against the Local Filesystem
                patch = repo.Diff.Compare<Patch>(new[] { filePath });
            }

            return patch.Content;
        });
    }

    /// <summary>
    /// Builds a committer/author signature from the repo config, or throws
    /// <see cref="GitIdentityMissingException"/> when no identity is configured.
    /// Every mutating operation must route through this instead of calling
    /// <c>repo.Config.BuildSignature</c> directly (which returns null on a fresh
    /// machine and crashes the caller) or falling back to a bogus placeholder
    /// identity (which pollutes history).
    /// </summary>
    private static Signature GetSignature(Repository repo)
    {
        // Capture one timestamp and reuse it so author/committer can't drift.
        var now = DateTimeOffset.Now;

        var sig = repo.Config.BuildSignature(now);
        if (sig != null) return sig;

        var name = repo.Config.Get<string>("user.name")?.Value;
        var email = repo.Config.Get<string>("user.email")?.Value;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email))
        {
            // Used by commit/pull/merge/rebase/stash/revert/cherry-pick, so keep
            // the message operation-agnostic.
            throw new GitIdentityMissingException(
                "No Git identity configured. Set your user.name and user.email before running Git operations.");
        }

        return new Signature(name, email, now);
    }

    public void Commit(string repoPath, string message)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            // Resolve the identity from config, throwing a typed error if unset.
            var signature = GetSignature(repo);

            // Commits whatever is currently in the Staging Index
            repo.Commit(message, signature, signature);
        });
    }


    private string? GetRemoteUrl(string repoPath, string remoteName)
    {
        using var repo = new Repository(repoPath);
        return repo.Network.Remotes[remoteName]?.Url;
    }

    private string? GetTokenForRemote(string repoPath, string remoteName)
        => GetTokenForUrl(GetRemoteUrl(repoPath, remoteName));

    // Resolves a stored token for the host of the given remote URL.
    private static string? GetTokenForUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;

        var (host, kind) = GitLoom.Core.Security.GitHostDetector.Detect(url);
        var keyring = new GitLoom.Core.Security.SecureKeyring();

        var token = string.IsNullOrEmpty(host)
            ? null
            : keyring.RetrieveSecret(GitLoom.Core.Security.GitHostDetector.TokenKeyForHost(host));

        // Back-compat: fall back to the legacy single "github_token" secret.
        if (string.IsNullOrEmpty(token) && kind == HostKind.GitHub)
        {
            token = keyring.RetrieveSecret("github_token");
        }
        return token;
    }

    public void Push(string repoPath)
    {
        // Remote transport goes through the git CLI (RunGitCheckedAuthenticated):
        // libgit2 has no SSH support, and the CLI path handles both HTTPS (token)
        // and SSH (rewritten to HTTPS when a token exists, otherwise SSH keys).
        bool needsUpstream = false;
        string branchName = "";
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Head;
            needsUpstream = branch.TrackedBranch == null;
            branchName = branch.FriendlyName;
        });

        if (needsUpstream)
            PushBranch(repoPath, branchName);
        else
            RunGitCheckedAuthenticated(repoPath, "origin", "push");
    }

    public void Pull(string repoPath)
    {
        Pull(repoPath, PullStrategy.Default);
    }

    public void Pull(string repoPath, PullStrategy strategy)
    {
        // Rebase strategy: fetch then replay the current branch onto its upstream.
        if (strategy == PullStrategy.Rebase)
        {
            Fetch(repoPath);
            var upstream = ExecuteWithRepo(repoPath, repo => repo.Head.TrackedBranch?.FriendlyName);
            if (string.IsNullOrEmpty(upstream))
                throw new GitOperationException("No upstream branch configured to rebase onto.");
            Rebase(repoPath, upstream);
            return;
        }

        // Remote transport goes through the git CLI (see Push). On conflicts git exits
        // non-zero; the finally converts that into a MergeConflictException the UI knows.
        try
        {
            if (strategy == PullStrategy.FastForwardOnly)
                RunGitCheckedAuthenticated(repoPath, "origin", "pull", "--ff-only");
            else
                RunGitCheckedAuthenticated(repoPath, "origin", "pull", "--no-rebase");
        }
        finally
        {
            var hasConflicts = ExecuteWithRepo(repoPath, repo => repo.Index.Conflicts.Any());
            if (hasConflicts)
                throw new MergeConflictException(
                    "Pull produced conflicts. Resolve the conflicted files in the Diff Viewer, then commit the merge.");
        }
    }

    public void Fetch(string repoPath, bool prune = false)
    {
        // Remote transport goes through the git CLI (see Push).
        if (prune)
            RunGitCheckedAuthenticated(repoPath, "origin", "fetch", "--prune");
        else
            RunGitCheckedAuthenticated(repoPath, "origin", "fetch");
    }
    public void Rebase(string repoPath, string targetBranchName)
    {
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var targetBranch = repo.Branches[targetBranchName];
                if (targetBranch == null) throw new GitOperationException($"Branch {targetBranchName} not found.");

                var signature = GetSignature(repo);
                var identity = new LibGit2Sharp.Identity(signature.Name, signature.Email);
                var rebaseResult = repo.Rebase.Start(repo.Head, targetBranch, null, identity, new RebaseOptions());

                if (rebaseResult.Status != RebaseStatus.Complete)
                {
                    // Do not abort! Leave the repository in the Rebasing state so the user can actually fix it.
                    throw new MergeConflictException($"Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then click 'Continue Rebase'.");
                }
            });
        }
        catch (LibGit2SharpException)
        {
            // Fallback to CLI if LibGit2Sharp outright fails (e.g., unsupported options)
            RunGitChecked(repoPath, "rebase", targetBranchName);
        }
    }

    public void Merge(string repoPath, string sourceBranchName)
    {
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var sourceBranch = repo.Branches[sourceBranchName];
                if (sourceBranch == null) throw new GitOperationException($"Branch {sourceBranchName} not found.");

                var signature = GetSignature(repo);

                var mergeResult = repo.Merge(sourceBranch, signature, new MergeOptions { CommitOnSuccess = false });

                if (mergeResult.Status == MergeStatus.Conflicts)
                {
                    throw new MergeConflictException($"Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then commit the merge.");
                }
            });
        }
        catch (LibGit2SharpException)
        {
            RunGitChecked(repoPath, "merge", "--no-commit", sourceBranchName);
        }
    }

    public bool IsMergeInProgress(string repoPath)
    {
        return System.IO.File.Exists(System.IO.Path.Combine(repoPath, ".git", "MERGE_HEAD"));
    }

    public string GetMergeMessage(string repoPath)
    {
        var msgPath = System.IO.Path.Combine(repoPath, ".git", "MERGE_MSG");
        if (System.IO.File.Exists(msgPath))
        {
            return System.IO.File.ReadAllText(msgPath).Trim();
        }
        return "Merge completed";
    }

    // --- Conflict resolution ---
    // Reads/writes the merge index stages (repo.Index.Conflicts) as the single source of truth.
    // Never parses working-tree conflict markers, and never commits on its own.

    public IReadOnlyList<ConflictedFile> GetConflicts(string repoPath) =>
        ExecuteWithRepo(repoPath, repo =>
            repo.Index.Conflicts
                .Select(c => new ConflictedFile
                {
                    // At least one stage is always present; prefer a non-null one for the path.
                    Path      = c.Ours?.Path ?? c.Theirs?.Path ?? c.Ancestor!.Path,
                    HasBase   = c.Ancestor != null,
                    HasOurs   = c.Ours     != null,
                    HasTheirs = c.Theirs   != null,
                })
                .OrderBy(f => f.Path, StringComparer.Ordinal)   // stable UI ordering
                .ToList());

    public (string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            var c = repo.Index.Conflicts[path]
                ?? throw new GitOperationException($"No conflict recorded for '{path}'.");

            string Read(IndexEntry? e) => e == null ? "" : repo.Lookup<Blob>(e.Id).GetContentText();

            return (Read(c.Ancestor), Read(c.Ours), Read(c.Theirs));
        });

    public void ResolveConflict(string repoPath, string path, string mergedContent) =>
        ExecuteWithRepo(repoPath, repo =>
        {
            var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);
            var dir = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir))
                System.IO.Directory.CreateDirectory(dir);      // subdirectory conflicts must work

            // UTF-8 without BOM (matches Git's expectations; avoids a spurious BOM diff).
            System.IO.File.WriteAllText(fullPath, mergedContent, new System.Text.UTF8Encoding(false));

            // Staging a conflicted path clears its stage-1/2/3 entries in the index.
            Commands.Stage(repo, path);
        });

    public bool HasUnresolvedConflicts(string repoPath) =>
        ExecuteWithRepo(repoPath, repo => repo.Index.Conflicts.Any());

    public bool IsRebasing(string repoPath)
    {
        return System.IO.Directory.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-merge")) ||
               System.IO.Directory.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-apply"));
    }

    public void ContinueRebase(string repoPath)
    {
        if (System.IO.File.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-merge", "interactive")))
        {
            // `git rebase --continue` can re-invoke GIT_EDITOR for the reword/squash steps
            // that come after the pause, so we must hand it the SAME message queue used at
            // start (keyed by SHA). Pointing it anywhere else silently loses those messages.
            var env = new System.Collections.Generic.Dictionary<string, string>
            {
                ["GIT_EDITOR"] = $"{GetSelfInvocationPrefix()} --rebase-msg \"{RebaseMsgQueueDir(repoPath)}\""
            };
            var (code, _, errStr) = RunGit(repoPath, env, default, "rebase", "--continue");

            if (code != 0)
            {
                using var repo = new Repository(repoPath);
                if (repo.Index.Conflicts.Any())
                    throw new MergeConflictException("Merge conflicts detected! Resolve the conflicts in the Diff Viewer, save the files to stage them, then click 'Continue Rebase' again.");

                var stoppedShaPath = System.IO.Path.Combine(repoPath, ".git", "rebase-merge", "stopped-sha");
                if (System.IO.File.Exists(stoppedShaPath))
                    throw new GitOperationException($"Rebase paused at {System.IO.File.ReadAllText(stoppedShaPath).Trim()} for editing. Amend your changes, then click 'Continue Rebase'.");

                throw new GitOperationException($"Continue rebase failed: {errStr}");
            }

            // Rebase finished — the queue is no longer needed.
            if (!System.IO.Directory.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-merge")))
            {
                try { System.IO.Directory.Delete(RebaseMsgQueueDir(repoPath), true); } catch { }
            }
            return;
        }

        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = GetSignature(repo);
            var identity = new LibGit2Sharp.Identity(signature.Name, signature.Email);

            var rebaseResult = repo.Rebase.Continue(identity, new RebaseOptions());

            // If it's Complete or Conflicts, that is a successful operation.
            // Complete = Rebase finished!
            // Conflicts = Successfully committed the previous resolution, but hit a new conflict in a subsequent commit.
            if (rebaseResult.Status != RebaseStatus.Complete && rebaseResult.Status != RebaseStatus.Conflicts)
            {
                throw new GitOperationException($"Rebase stopped with status: {rebaseResult.Status}");
            }
        });
    }

    public void AbortRebase(string repoPath)
    {
        if (System.IO.File.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-merge", "interactive")))
        {
            var (code, _, errStr) = RunGit(repoPath, "rebase", "--abort");
            // Whether or not abort reports success, the message queue is now stale.
            try { System.IO.Directory.Delete(RebaseMsgQueueDir(repoPath), true); } catch { }
            if (code != 0) throw new GitOperationException($"Abort rebase failed: {errStr}");
            return;
        }

        ExecuteWithRepo(repoPath, repo =>
        {
            repo.Rebase.Abort();
        });
    }

    public void UpdateProject(string repoPath)
    {
        Fetch(repoPath, prune: true);

        // Fast-forward every non-current tracking branch to its upstream (pure ref
        // updates, no working-tree changes), and note whether the checked-out branch
        // also needs updating so it can be pulled through the normal path below.
        bool pullCurrentBranch = ExecuteWithRepo(repoPath, repo =>
        {
            bool needsPull = false;
            var localBranches = repo.Branches.Where(b => !b.IsRemote && b.TrackedBranch != null).ToList();
            foreach (var branch in localBranches)
            {
                try
                {
                    if (branch.IsCurrentRepositoryHead)
                    {
                        // The checked-out branch touches the working tree — defer it to
                        // the CLI Pull path (below), which handles SSH/HTTPS and conflicts.
                        needsPull = true;
                        continue;
                    }

                    var trackingBranch = branch.TrackedBranch;

                    // A just-created local branch or an unborn upstream can have a
                    // null Tip; skip fast-forward evaluation rather than crashing.
                    if (branch.Tip == null || trackingBranch.Tip == null) continue;

                    var baseCommit = repo.ObjectDatabase.FindMergeBase(branch.Tip, trackingBranch.Tip);

                    // If it's a fast-forward (local branch hasn't diverged)
                    if (baseCommit?.Id == branch.Tip.Id && branch.Tip.Id != trackingBranch.Tip.Id)
                    {
                        repo.Refs.UpdateTarget(repo.Refs[branch.CanonicalName], trackingBranch.Tip.Id);
                    }
                }
                catch (LibGit2SharpException ex)
                {
                    // Surface the failure as a typed error (with branch context)
                    // rather than swallowing non-conflict failures silently.
                    throw new GitOperationException($"Failed to update branch '{branch.FriendlyName}': {ex.Message}", ex);
                }
            }
            return needsPull;
        });

        // Pull the checked-out branch via the CLI path so it works for SSH- and
        // HTTPS-cloned remotes alike (and raises MergeConflictException on conflicts).
        if (pullCurrentBranch)
            Pull(repoPath);
    }

    // Single hardened, cross-platform git runner. Replaces the old Windows-only
    // ExecuteGitCli (cmd.exe + "|| pause", which popped a visible terminal, broke
    // on macOS/Linux, and discarded stderr) and the near-duplicate silent runner.
    //
    // Arguments are passed via ArgumentList (never a single command string) so
    // there is no shell quoting/injection surface — each element is one argv slot.
    internal static (int Code, string Out, string Err) RunGit(string repoPath, params string[] args)
        => RunGit(repoPath, null, default, args);

    // Directory (under .git) where the interactive-rebase message queue lives. It is
    // deliberately inside .git so it survives conflict/edit pauses and is reused by
    // ContinueRebase — the reword/squash messages are keyed by original commit SHA.
    internal static string RebaseMsgQueueDir(string repoPath)
        => System.IO.Path.Combine(repoPath, ".git", "gitloom-rebase-msg");

    // Builds the quoted command prefix that re-invokes THIS application as a git
    // sequence/message editor. Handles the framework-dependent case where the process
    // host is `dotnet` (MainModule points at the runtime, not our app) by expanding to
    // `"dotnet" "<app>.dll"`, and works for the single-file/apphost case on every OS.
    internal static string GetSelfInvocationPrefix()
    {
        var host = Environment.ProcessPath;
        if (string.IsNullOrEmpty(host))
        {
            try { host = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName; }
            catch { host = null; }
        }

        var entryDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;

        if (!string.IsNullOrEmpty(host))
        {
            var hostName = System.IO.Path.GetFileNameWithoutExtension(host);
            if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(entryDll))
            {
                return $"\"{host}\" \"{entryDll}\"";
            }
            return $"\"{host}\"";
        }

        if (!string.IsNullOrEmpty(entryDll))
            return $"\"dotnet\" \"{entryDll}\"";

        return "\"GitLoom.App\"";
    }

    internal static (int Code, string Out, string Err) RunGit(
        string repoPath, IReadOnlyDictionary<string, string>? environment, System.Threading.CancellationToken ct, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Never let git block on an interactive credential/terminal prompt: there
        // is no TTY behind the GUI, so a prompt would hang the operation forever.
        // Set as the default first so an explicit override in `environment` wins.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (environment != null)
        {
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;
        }

        System.Diagnostics.Process process;
        try
        {
            process = System.Diagnostics.Process.Start(psi)
                ?? throw new GitOperationException("Failed to launch git. Is Git installed and on the PATH?");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // git not on PATH (or not executable) surfaces as a Win32Exception —
            // wrap it so callers always receive a typed GitOperationException.
            throw new GitOperationException("Failed to launch git. Is Git installed and on the PATH?", ex);
        }

        using (process)
        using (var reg = ct.Register(() => { try { process.Kill(true); } catch { } }))
        {
            // Close stdin so any prompt that slips past GIT_TERMINAL_PROMPT hits EOF
            // and fails fast instead of waiting for input that will never come.
            process.StandardInput.Close();

            // Read both streams concurrently to avoid a pipe-buffer deadlock.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            ct.ThrowIfCancellationRequested();

            return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
    }

    // Joins args for an error message with any URL-embedded credentials masked,
    // so a token-bearing remote URL never leaks into a UI error surface or log.
    private static string RedactArgs(System.Collections.Generic.IEnumerable<string> args)
        => string.Join(' ', args.Select(RedactArg));

    private static string RedactArg(string arg)
    {
        var schemeIdx = arg.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            var schemeEnd = schemeIdx + 3;
            var at = arg.IndexOf('@', schemeEnd);
            if (at > schemeEnd)
                return string.Concat(arg.AsSpan(0, schemeEnd), "***@", arg.AsSpan(at + 1));
        }
        return arg;
    }

    // Runs git and throws a typed exception (with captured stderr) on failure so
    // the app can show a real error panel instead of a terminal pop-up.
    private void RunGitChecked(string repoPath, params string[] args)
        => RunGitChecked(repoPath, null, args);

    private void RunGitChecked(string repoPath, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var (code, _, err) = RunGit(repoPath, environment, default, args);
        if (code == 0) return;

        if (err.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationRequiredException(err);
        }

        var message = string.IsNullOrWhiteSpace(err)
            ? $"git {RedactArgs(args)} failed with exit code {code}."
            : err;
        throw new GitOperationException(message);
    }

    /// <summary>
    /// Runs a git command against the given remote, injecting a token via git's
    /// credential mechanism when one is available for that remote's host. The
    /// token is passed in the child process ENVIRONMENT and read by an inline
    /// credential helper — it never appears in argv/process listings, shell
    /// history, or the remote URL (the pre-1.7 leak vector).
    /// </summary>
    private void RunGitCheckedAuthenticated(string repoPath, string remoteName, params string[] args)
    {
        var url = GetRemoteUrl(repoPath, remoteName);
        var (_, kind) = GitLoom.Core.Security.GitHostDetector.Detect(url ?? "");
        var token = GetTokenForRemote(repoPath, remoteName);

        if (string.IsNullOrEmpty(token))
        {
            // No stored token: let git use its own credential helpers / prompts,
            // but never block on an interactive prompt in the GUI.
            RunGitChecked(repoPath, new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" }, args);
            return;
        }

        var username = GitLoom.Core.Security.GitHostDetector.UsernameForToken(kind);
        // Inline helper echoes credentials from $GITLOOM_TOKEN; only the helper
        // script text is in argv, never the secret.
        var helper = $"!f() {{ echo \"username={username}\"; echo \"password=$GITLOOM_TOKEN\"; }}; f";
        var fullArgs = new List<string> { "-c", "credential.helper=", "-c", $"credential.helper={helper}" };

        // libgit2 has no SSH transport, so remote ops run through the git CLI. If the
        // repo was cloned over SSH (git@host:… / ssh://…) but we hold an HTTPS token
        // for that host, transparently rewrite the remote to HTTPS for this one command
        // so token auth works without an SSH key. Scoped via -c insteadOf — the stored
        // remote URL is left untouched. Only done when a token exists, so genuine
        // SSH-key setups (no token) still use SSH via the CLI.
        var httpsUrl = GitLoom.Core.Security.GitHostDetector.ToHttpsUrl(url ?? "");
        if (!string.IsNullOrEmpty(httpsUrl) && !string.Equals(httpsUrl, url, StringComparison.Ordinal))
        {
            fullArgs.Add("-c");
            fullArgs.Add($"url.{httpsUrl}.insteadOf={url}");
        }

        fullArgs.AddRange(args);

        var env = new Dictionary<string, string>
        {
            ["GITLOOM_TOKEN"] = token,
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        RunGitChecked(repoPath, env, fullArgs.ToArray());
    }

    public void PushWithCredentials(string repoPath, string username, string password)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Head;
            if (branch.TrackedBranch == null) throw new GitOperationException("No upstream branch configured.");

            var options = new PushOptions
            {
                CredentialsProvider = (_url, _user, _cred) => new UsernamePasswordCredentials
                {
                    Username = username,
                    Password = password
                }
            };
            repo.Network.Push(branch, options);
        });
    }

    public void PullWithCredentials(string repoPath, string username, string password)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = GetSignature(repo);
            var options = new PullOptions
            {
                FetchOptions = new FetchOptions
                {
                    CredentialsProvider = (_url, _user, _cred) => new UsernamePasswordCredentials
                    {
                        Username = username,
                        Password = password
                    }
                }
            };
            // Mirror Pull (audit 1.5): Commands.Pull reports conflicts through the
            // MergeResult rather than throwing, so ignoring it would leave the
            // working tree silently conflicted with no signal to the user.
            var result = Commands.Pull(repo, signature, options);
            if (result.Status == MergeStatus.Conflicts)
                throw new MergeConflictException(
                    "Pull produced conflicts. Resolve the conflicted files in the Diff Viewer, then commit the merge.");
        });
    }

    public (int? Ahead, int? Behind) GetAheadBehind(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Head;
            if (branch.TrackedBranch == null) return (null, null);

            return (branch.TrackingDetails.AheadBy, branch.TrackingDetails.BehindBy);
        });
    }

    public IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take, CommitSearchFilter? searchFilter = null)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            IEnumerable<Commit> query;

            if (searchFilter?.FilePaths != null && searchFilter.FilePaths.Count == 1)
            {
                // Single path: LibGit2Sharp's history-following walk is the most
                // efficient option and streams lazily.
                query = repo.Commits.QueryBy(searchFilter.FilePaths.First()).Select(e => e.Commit);
            }
            else
            {
                var filter = new CommitFilter
                {
                    SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time
                };

                if (!string.IsNullOrEmpty(searchFilter?.BranchName))
                {
                    var branch = repo.Branches[searchFilter.BranchName];
                    if (branch != null)
                    {
                        filter.IncludeReachableFrom = branch;
                    }
                }
                query = repo.Commits.QueryBy(filter);

                // Multi-path: run a SINGLE lazy walk (already sorted topo+time)
                // and test each commit's tree changes for membership, instead of
                // materializing the full history of every path and re-sorting it
                // in memory (which broke pagination and desynced the graph fringe).
                if (searchFilter?.FilePaths != null && searchFilter.FilePaths.Count > 1)
                {
                    var paths = new HashSet<string>(searchFilter.FilePaths, StringComparer.Ordinal);
                    query = query.Where(c => CommitTouchesAnyPath(repo, c, paths));
                }
            }

            if (!string.IsNullOrEmpty(searchFilter?.Text))
            {
                var text = searchFilter.Text;
                query = query.Where(c =>
                    c.Message.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    c.Sha.StartsWith(text, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(searchFilter?.Author))
            {
                var author = searchFilter.Author;
                query = query.Where(c =>
                    c.Author.Name.Contains(author, StringComparison.OrdinalIgnoreCase) ||
                    c.Author.Email.Contains(author, StringComparison.OrdinalIgnoreCase));
            }

            if (searchFilter?.DateFrom.HasValue == true)
            {
                query = query.Where(c => c.Author.When.DateTime >= searchFilter.DateFrom.Value);
            }

            if (searchFilter?.DateTo.HasValue == true)
            {
                query = query.Where(c => c.Author.When.DateTime <= searchFilter.DateTo.Value);
            }

            return query.Skip(skip).Take(take).Select(c => new GitCommitItem
            {
                Sha = c.Sha,
                ParentShas = c.Parents.Select(p => p.Sha).ToList(),
                Message = c.Message,
                MessageShort = c.MessageShort,
                AuthorName = c.Author.Name,
                AuthorEmail = c.Author.Email,
                CommitDate = c.Author.When
            }).ToList();
        });
    }

    /// <summary>
    /// True if <paramref name="commit"/> added, modified, deleted or renamed any
    /// of <paramref name="paths"/> relative to its first parent (or the empty
    /// tree for a root commit).
    /// </summary>
    private static bool CommitTouchesAnyPath(Repository repo, Commit commit, HashSet<string> paths)
    {
        var parentTree = commit.Parents.FirstOrDefault()?.Tree;
        var changes = repo.Diff.Compare<TreeChanges>(parentTree, commit.Tree);
        foreach (var change in changes)
        {
            if (paths.Contains(change.Path) ||
                (!string.IsNullOrEmpty(change.OldPath) && paths.Contains(change.OldPath)))
            {
                return true;
            }
        }
        return false;
    }

    public IEnumerable<GitBranchItem> GetBranches(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var branches = new System.Collections.Generic.List<GitBranchItem>();
            foreach (var branch in repo.Branches)
            {
                branches.Add(new GitBranchItem
                {
                    Name = branch.FriendlyName,
                    FriendlyName = branch.FriendlyName,
                    IsRemote = branch.IsRemote,
                    IsCurrentRepositoryHead = branch.IsCurrentRepositoryHead,
                    TipSha = branch.Tip?.Sha ?? string.Empty
                });
            }
            return branches;
        });
    }

    public void CheckoutBranch(string repoPath, string branchName)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new GitOperationException($"Branch {branchName} not found.");

            if (branch.IsRemote)
            {
                // For remote branches, we must create a local tracking branch and check that out instead.
                // Capture the remote branch's canonical name and tip up front — we
                // reassign `branch` to the local branch below and must not re-index
                // the remote afterwards (the original code re-looked it up).
                var remoteCanonicalName = branch.CanonicalName;
                var remoteTip = branch.Tip;
                var localBranchName = branchName.Contains("/") ? branchName.Substring(branchName.IndexOf("/") + 1) : branchName;

                // Check if local branch already exists
                var existingLocal = repo.Branches[localBranchName];
                if (existingLocal != null)
                {
                    branch = existingLocal;
                }
                else
                {
                    var newLocal = repo.CreateBranch(localBranchName, remoteTip);
                    repo.Branches.Update(newLocal, b => b.TrackedBranch = remoteCanonicalName);
                    branch = newLocal;
                }
            }

            Commands.Checkout(repo, branch);
        });
    }

    public void CreateBranch(string repoPath, string branchName, string baseBranchName, bool checkout)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var baseBranch = string.IsNullOrEmpty(baseBranchName) ? repo.Head : repo.Branches[baseBranchName];
            if (baseBranch == null) throw new GitOperationException($"Base branch '{baseBranchName}' not found.");
            if (baseBranch.Tip == null)
                throw new GitOperationException("Cannot create a branch from a base that has no commits yet. Make an initial commit first.");

            var newBranch = repo.CreateBranch(branchName, baseBranch.Tip);
            if (checkout)
            {
                Commands.Checkout(repo, newBranch);
            }
        });
    }

    public void RenameBranch(string repoPath, string oldName, string newName)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[oldName];
            if (branch == null) throw new GitOperationException($"Branch '{oldName}' not found.");
            repo.Branches.Rename(branch, newName);
        });
    }

    public void PushBranch(string repoPath, string branchName)
    {
        // Pushes the branch to origin and sets it as upstream, over the git CLI (see Push).
        RunGitCheckedAuthenticated(repoPath, "origin", "push", "-u", "origin", branchName);
    }

    public void DeleteBranch(string repoPath, string branchName, bool force = false)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new GitOperationException($"Branch {branchName} not found.");

            if (branch.IsRemote)
            {
                // Fallback to CLI to delete remote branch safely
                var remoteName = branch.RemoteName;
                var remoteBranchName = branchName.Replace($"{remoteName}/", "");
                RunGitCheckedAuthenticated(repoPath, remoteName, "push", remoteName, "--delete", remoteBranchName);
            }
            else
            {
                repo.Branches.Remove(branch);
            }
        });
    }

    public bool HasUncommittedChanges(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var status = repo.RetrieveStatus();
            return status.IsDirty;
        });
    }

    public void StashChanges(string repoPath, string message)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = GetSignature(repo);
            repo.Stashes.Add(signature, message, LibGit2Sharp.StashModifiers.Default);
        });
    }

    public IEnumerable<GitStashItem> GetStashes(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var result = new List<GitStashItem>();
            int i = 0;
            foreach (var stash in repo.Stashes)
            {
                result.Add(new GitStashItem { Index = i, Message = stash.Message });
                i++;
            }
            return result;
        });
    }

    public void StashPush(string repoPath, string message)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = GetSignature(repo);

            repo.Stashes.Add(signature, message, StashModifiers.Default);
        });
    }

    public void StashDrop(string repoPath, int stashIndex)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            repo.Stashes.Remove(stashIndex);
        });
    }

    public void StashPop(string repoPath, int stashIndex)
    {
        // LibGit2Sharp lacks Apply/Pop natively, fallback to silent CLI
        RunGitChecked(repoPath, "stash", "pop", $"stash@{{{stashIndex}}}");
    }

    public void StashApply(string repoPath, int stashIndex)
    {
        // LibGit2Sharp lacks Apply/Pop natively, fallback to silent CLI
        RunGitChecked(repoPath, "stash", "apply", $"stash@{{{stashIndex}}}");
    }



    public IEnumerable<string> ListWorktrees(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var worktrees = new System.Collections.Generic.List<string>();
            foreach (var wt in repo.Worktrees)
            {
                worktrees.Add(wt.Name);
            }
            return worktrees;
        });
    }

    public void AddWorktree(string repoPath, string worktreePath, string branchName)
    {
        RunGitChecked(repoPath, "worktree", "add", worktreePath, branchName);
    }

    public void RemoveWorktree(string repoPath, string worktreePath)
    {
        RunGitChecked(repoPath, "worktree", "remove", worktreePath);
    }

    public string GetDiffAgainstCommit(string repoPath, string commitSha, string filePath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new GitOperationException($"Commit {commitSha} not found.");

            var patch = repo.Diff.Compare<Patch>(commit.Tree, DiffTargets.WorkingDirectory, new[] { filePath });
            return patch?.Content ?? string.Empty;
        });
    }

    public string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new GitOperationException($"Branch {branchName} not found.");
            if (branch.Tip == null) throw new GitOperationException($"Branch '{branchName}' has no commits yet.");

            var patch = repo.Diff.Compare<Patch>(branch.Tip.Tree, DiffTargets.WorkingDirectory);
            return patch?.Content ?? string.Empty;
        });
    }

    public IEnumerable<string> GetCommitModifiedFiles(string repoPath, string commitSha)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) return System.Linq.Enumerable.Empty<string>();

            if (!commit.Parents.Any())
            {
                var changes = repo.Diff.Compare<TreeChanges>(null, commit.Tree);
                return changes.Select(c => c.Path).ToList();
            }
            else
            {
                var parentCommit = commit.Parents.First();
                var changes = repo.Diff.Compare<TreeChanges>(parentCommit.Tree, commit.Tree);
                return changes.Select(c => c.Path).ToList();
            }
        });
    }

    public IEnumerable<string> GetBranchesContainingCommit(string repoPath, string commitSha)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) return System.Linq.Enumerable.Empty<string>();

            var branches = new System.Collections.Generic.List<string>();
            foreach (var branch in repo.Branches)
            {
                // Skip unborn branches (no tip) — FindMergeBase would throw on null.
                if (branch.Tip == null) continue;

                var mergeBase = repo.ObjectDatabase.FindMergeBase(commit, branch.Tip);
                if (mergeBase != null && mergeBase.Sha == commit.Sha)
                {
                    branches.Add(branch.FriendlyName);
                }
            }
            return branches;
        });
    }
    public IEnumerable<string> GetAuthors(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            return repo.Commits.Select(c => c.Author.Name).Distinct().OrderBy(a => a).ToList();
        });
    }

    public IEnumerable<string> GetRepositoryPaths(string repoPath)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            return repo.Index.Select(i => i.Path).OrderBy(p => p).ToList();
        });
    }

    public void CheckoutRevision(string repoPath, string commitSha)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit != null)
            {
                Commands.Checkout(repo, commit);
            }
        });
    }

    public void ResetToCommit(string repoPath, string commitSha, LibGit2Sharp.ResetMode mode)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit != null)
            {
                repo.Reset(mode, commit);
            }
        });
    }

    public void RevertCommit(string repoPath, string commitSha)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit != null)
            {
                var author = GetSignature(repo);
                repo.Revert(commit, author, new RevertOptions { CommitOnSuccess = true });
            }
        });
    }

    public void CherryPick(string repoPath, string commitSha)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new GitOperationException($"Commit {commitSha} not found.");

            var signature = GetSignature(repo);
            var result = repo.CherryPick(commit, signature, new CherryPickOptions { CommitOnSuccess = false });

            if (result.Status == CherryPickStatus.Conflicts)
            {
                throw new MergeConflictException("Cherry pick resulted in conflicts. Please resolve them and commit manually.");
            }
        });
    }

    public void AmendCommitMessage(string repoPath, string commitSha, string newMessage)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new GitOperationException("Commit not found");

            if (repo.Head.Tip == null)
            {
                throw new GitOperationException("There is no commit to amend (the branch is unborn).");
            }

            if (repo.Head.Tip.Sha != commit.Sha)
            {
                throw new GitOperationException("Can only amend the most recent commit (HEAD).");
            }

            var signature = GetSignature(repo);
            repo.Commit(newMessage, signature, signature, new CommitOptions { AmendPreviousCommit = true });
        });
    }
}
