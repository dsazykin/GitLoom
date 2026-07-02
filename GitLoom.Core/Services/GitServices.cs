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

            foreach (var path in paths)
            {
                var entry = status[path];
                if (entry != null && (entry.State.HasFlag(FileStatus.NewInWorkdir) || entry.State.HasFlag(FileStatus.NewInIndex)))
                {
                    // Untracked/New file, delete it
                    var fullPath = System.IO.Path.Combine(repo.Info.WorkingDirectory, path);
                    if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
                    if (System.IO.Directory.Exists(fullPath)) System.IO.Directory.Delete(fullPath, true);
                }
                else
                {
                    trackedToCheckout.Add(path);
                }
            }

            if (trackedToCheckout.Count > 0)
            {
                repo.CheckoutPaths(repo.Head.FriendlyName, trackedToCheckout.ToArray(), new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });
            }
        });
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

    public void Commit(string repoPath, string message)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            // Automatically extracts Name and Email from the user's global ~/.gitconfig
            var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);

            // Commits whatever is currently in the Staging Index
            repo.Commit(message, signature, signature);
        });
    }

    // Credentials handler for the LibGit2Sharp path, keyed by the remote's host.
    private LibGit2Sharp.Handlers.CredentialsHandler? GetCredentialsProvider(string repoPath)
    {
        var url = GetRemoteUrl(repoPath, "origin");
        var (_, kind) = GitLoom.Core.Security.GitHostDetector.Detect(url ?? "");
        var token = GetTokenForRemote(repoPath, "origin");
        if (string.IsNullOrEmpty(token)) return null;

        var username = GitLoom.Core.Security.GitHostDetector.UsernameForToken(kind);
        return (_url, _user, _cred) => new UsernamePasswordCredentials
        {
            Username = username,
            Password = token
        };
    }

    private string? GetRemoteUrl(string repoPath, string remoteName)
    {
        using var repo = new Repository(repoPath);
        return repo.Network.Remotes[remoteName]?.Url;
    }

    private string? GetTokenForRemote(string repoPath, string remoteName)
    {
        var url = GetRemoteUrl(repoPath, remoteName);
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
        try
        {
            bool needsUpstream = false;
            string branchName = "";
            ExecuteWithRepo(repoPath, repo =>
            {
                var branch = repo.Head;
                if (branch.TrackedBranch == null)
                {
                    needsUpstream = true;
                    branchName = branch.FriendlyName;
                }
                else
                {
                    var options = new PushOptions();
                    var creds = GetCredentialsProvider(repoPath);
                    if (creds != null) options.CredentialsProvider = creds;

                    repo.Network.Push(branch, options);
                }
            });

            if (needsUpstream)
            {
                // Use PushBranch which configures the upstream and pushes using LibGit2Sharp and our SecureKeyring
                PushBranch(repoPath, branchName);
            }
        }
        catch (LibGit2SharpException)
        {
            // If it crashes due to SSH or Credentials, fallback to the native terminal Git!
            bool needsUpstream = false;
            string branchName = "";
            ExecuteWithRepo(repoPath, repo =>
            {
                var branch = repo.Head;
                if (branch.TrackedBranch == null)
                {
                    needsUpstream = true;
                }
                branchName = branch.FriendlyName;
            });

            if (needsUpstream)
                RunGitCheckedAuthenticated(repoPath, "origin", "push", "--set-upstream", "origin", branchName);
            else
                RunGitCheckedAuthenticated(repoPath, "origin", "push");
        }
    }

    public void Pull(string repoPath)
    {
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
                var options = new PullOptions
                {
                    FetchOptions = new FetchOptions()
                };
                var creds = GetCredentialsProvider(repoPath);
                if (creds != null) options.FetchOptions.CredentialsProvider = creds;

                Commands.Pull(repo, signature, options);
            });
        }
        catch (LibGit2SharpException)
        {
            RunGitCheckedAuthenticated(repoPath, "origin", "pull");
        }
    }

    public void Fetch(string repoPath, bool prune = false)
    {
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var remote = repo.Network.Remotes["origin"];
                if (remote != null)
                {
                    var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                    var fetchOptions = new FetchOptions();
                    if (prune) fetchOptions.Prune = true;

                    var creds = GetCredentialsProvider(repoPath);
                    if (creds != null) fetchOptions.CredentialsProvider = creds;

                    Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, "");
                }
            });
        }
        catch (LibGit2SharpException)
        {
            if (prune) RunGitCheckedAuthenticated(repoPath, "origin", "fetch", "--prune");
            else RunGitCheckedAuthenticated(repoPath, "origin", "fetch");
        }
    }
    public void Rebase(string repoPath, string targetBranchName)
    {
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var targetBranch = repo.Branches[targetBranchName];
                if (targetBranch == null) throw new System.Exception($"Branch {targetBranchName} not found.");

                var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
                var identity = new LibGit2Sharp.Identity(signature.Name, signature.Email);
                var rebaseResult = repo.Rebase.Start(repo.Head, targetBranch, null, identity, new RebaseOptions());

                if (rebaseResult.Status != RebaseStatus.Complete)
                {
                    // Do not abort! Leave the repository in the Rebasing state so the user can actually fix it.
                    throw new System.Exception($"Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then click 'Continue Rebase'.");
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
                if (sourceBranch == null) throw new System.Exception($"Branch {sourceBranchName} not found.");

                var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
                signature ??= new Signature("GitLoom", "gitloom@localhost", System.DateTimeOffset.Now);

                var mergeResult = repo.Merge(sourceBranch, signature, new MergeOptions { CommitOnSuccess = false });

                if (mergeResult.Status == MergeStatus.Conflicts)
                {
                    throw new System.Exception($"Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then commit the merge.");
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

    public bool IsRebasing(string repoPath)
    {
        return System.IO.Directory.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-merge")) ||
               System.IO.Directory.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-apply"));
    }

    public void ContinueRebase(string repoPath)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
            signature ??= new Signature("GitLoom", "gitloom@localhost", System.DateTimeOffset.Now);
            var identity = new LibGit2Sharp.Identity(signature.Name, signature.Email);

            var rebaseResult = repo.Rebase.Continue(identity, new RebaseOptions());

            // If it's Complete or Conflicts, that is a successful operation.
            // Complete = Rebase finished!
            // Conflicts = Successfully committed the previous resolution, but hit a new conflict in a subsequent commit.
            if (rebaseResult.Status != RebaseStatus.Complete && rebaseResult.Status != RebaseStatus.Conflicts)
            {
                throw new System.Exception($"Rebase stopped with status: {rebaseResult.Status}");
            }
        });
    }

    public void AbortRebase(string repoPath)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            repo.Rebase.Abort();
        });
    }

    public void UpdateProject(string repoPath)
    {
        Fetch(repoPath, prune: true);

        ExecuteWithRepo(repoPath, repo =>
        {
            var localBranches = repo.Branches.Where(b => !b.IsRemote && b.TrackedBranch != null).ToList();
            foreach (var branch in localBranches)
            {
                try
                {
                    if (branch.IsCurrentRepositoryHead)
                    {
                        var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
                        Commands.Pull(repo, signature, new PullOptions());
                    }
                    else
                    {
                        var trackingBranch = branch.TrackedBranch;
                        var baseCommit = repo.ObjectDatabase.FindMergeBase(branch.Tip, trackingBranch.Tip);

                        // If it's a fast-forward (local branch hasn't diverged)
                        if (baseCommit?.Id == branch.Tip.Id && branch.Tip.Id != trackingBranch.Tip.Id)
                        {
                            repo.Refs.UpdateTarget(repo.Refs[branch.CanonicalName], trackingBranch.Tip.Id);
                        }
                    }
                }
                catch (LibGit2SharpException ex)
                {
                    // If there is a merge conflict on the current branch, it throws. We break and leave the repo on this branch so the user can resolve it!
                    if (ex.Message.Contains("conflict", System.StringComparison.OrdinalIgnoreCase))
                    {
                        throw new System.Exception($"Merge conflict occurred on branch '{branch.FriendlyName}'. Please resolve conflicts.");
                    }
                }
            }
        });
    }

    // Single hardened, cross-platform git runner. Replaces the old Windows-only
    // ExecuteGitCli (cmd.exe + "|| pause", which popped a visible terminal, broke
    // on macOS/Linux, and discarded stderr) and the near-duplicate silent runner.
    //
    // Arguments are passed via ArgumentList (never a single command string) so
    // there is no shell quoting/injection surface — each element is one argv slot.
    private static (int Code, string Out, string Err) RunGit(string repoPath, params string[] args)
        => RunGit(repoPath, null, args);

    private static (int Code, string Out, string Err) RunGit(
        string repoPath, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (environment != null)
        {
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;
        }

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new GitOperationException("Failed to launch git. Is Git installed and on the PATH?");

        // Read both streams concurrently to avoid a pipe-buffer deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    // Runs git and throws a typed exception (with captured stderr) on failure so
    // the app can show a real error panel instead of a terminal pop-up.
    private void RunGitChecked(string repoPath, params string[] args)
        => RunGitChecked(repoPath, null, args);

    private void RunGitChecked(string repoPath, IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        var (code, _, err) = RunGit(repoPath, environment, args);
        if (code == 0) return;

        if (err.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationRequiredException(err);
        }

        var message = string.IsNullOrWhiteSpace(err)
            ? $"git {string.Join(' ', args)} failed with exit code {code}."
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
            if (branch.TrackedBranch == null) throw new System.Exception("No upstream branch configured.");

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
            var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
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
            Commands.Pull(repo, signature, options);
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

            if (searchFilter?.FilePaths != null && searchFilter.FilePaths.Any())
            {
                if (searchFilter.FilePaths.Count == 1)
                {
                    query = repo.Commits.QueryBy(searchFilter.FilePaths.First()).Select(e => e.Commit);
                }
                else
                {
                    query = searchFilter.FilePaths
                        .SelectMany(path => repo.Commits.QueryBy(path).Select(e => e.Commit))
                        .Distinct()
                        .OrderByDescending(c => c.Author.When);
                }
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
            }

            if (!string.IsNullOrEmpty(searchFilter?.Text))
            {
                var text = searchFilter.Text.ToLowerInvariant();
                query = query.Where(c => c.Message.ToLowerInvariant().Contains(text) || c.Sha.StartsWith(text));
            }

            if (!string.IsNullOrEmpty(searchFilter?.Author))
            {
                var author = searchFilter.Author.ToLowerInvariant();
                query = query.Where(c => c.Author.Name.ToLowerInvariant().Contains(author) || c.Author.Email.ToLowerInvariant().Contains(author));
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
            if (branch == null) throw new System.Exception($"Branch {branchName} not found.");

            if (branch.IsRemote)
            {
                // For remote branches, we must create a local tracking branch and check that out instead
                var localBranchName = branchName.Contains("/") ? branchName.Substring(branchName.IndexOf("/") + 1) : branchName;

                // Check if local branch already exists
                var existingLocal = repo.Branches[localBranchName];
                if (existingLocal != null)
                {
                    branch = existingLocal;
                }
                else
                {
                    branch = repo.CreateBranch(localBranchName, branch.Tip);
                    repo.Branches.Update(branch, b => b.TrackedBranch = repo.Branches[branchName].CanonicalName);
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
            if (baseBranch == null) throw new System.Exception($"Base branch '{baseBranchName}' not found.");

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
            if (branch == null) throw new System.Exception($"Branch '{oldName}' not found.");
            repo.Branches.Rename(branch, newName);
        });
    }

    public void PushBranch(string repoPath, string branchName)
    {
        try
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var branch = repo.Branches[branchName];
                if (branch == null) throw new System.Exception($"Branch '{branchName}' not found.");

                var remote = repo.Network.Remotes["origin"];
                if (remote != null)
                {
                    repo.Branches.Update(branch, b => b.Remote = remote.Name, b => b.UpstreamBranch = branch.CanonicalName);
                }

                var options = new PushOptions();
                var creds = GetCredentialsProvider(repoPath);
                if (creds != null) options.CredentialsProvider = creds;

                repo.Network.Push(branch, options);
            });
        }
        catch (LibGit2SharpException)
        {
            RunGitCheckedAuthenticated(repoPath, "origin", "push", "-u", "origin", branchName);
        }
    }

    public void DeleteBranch(string repoPath, string branchName, bool force = false)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new System.Exception($"Branch {branchName} not found.");

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
            var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
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
            var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
            // Fallback to dummy signature if none is set
            signature ??= new Signature("GitLoom", "gitloom@localhost", System.DateTimeOffset.Now);

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
            if (commit == null) throw new System.Exception($"Commit {commitSha} not found.");

            var patch = repo.Diff.Compare<Patch>(commit.Tree, DiffTargets.WorkingDirectory, new[] { filePath });
            return patch?.Content ?? string.Empty;
        });
    }

    public string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName)
    {
        return ExecuteWithRepo(repoPath, repo =>
        {
            var branch = repo.Branches[branchName];
            if (branch == null) throw new System.Exception($"Branch {branchName} not found.");

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
                var author = repo.Config.BuildSignature(DateTimeOffset.Now);
                repo.Revert(commit, author, new RevertOptions { CommitOnSuccess = true });
            }
        });
    }

    public void CherryPick(string repoPath, string commitSha)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new ArgumentException($"Commit {commitSha} not found.");

            var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
            var result = repo.CherryPick(commit, signature, new CherryPickOptions { CommitOnSuccess = false });

            if (result.Status == CherryPickStatus.Conflicts)
            {
                throw new Exception("Cherry pick resulted in conflicts. Please resolve them and commit manually.");
            }
        });
    }

    public void AmendCommitMessage(string repoPath, string commitSha, string newMessage)
    {
        ExecuteWithRepo(repoPath, repo =>
        {
            var commit = repo.Lookup<Commit>(commitSha);
            if (commit == null) throw new Exception("Commit not found");

            if (repo.Head.Tip.Sha != commit.Sha)
            {
                throw new Exception("Can only amend the most recent commit (HEAD).");
            }

            var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
            repo.Commit(newMessage, signature, signature, new CommitOptions { AmendPreviousCommit = true });
        });
    }
}
