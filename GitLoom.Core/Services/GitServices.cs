using System;
using System.IO;
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
                        repo.Network.Push(branch, new PushOptions());
                    }
                });

                if (needsUpstream)
                {
                    ExecuteGitCli(repoPath, $"push --set-upstream origin \"{branchName}\"");
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
                        branchName = branch.FriendlyName;
                    }
                });

                if (needsUpstream)
                {
                    ExecuteGitCli(repoPath, $"push --set-upstream origin \"{branchName}\"");
                }
                else
                {
                    ExecuteGitCli(repoPath, "push");
                }
            }
        }

        public void Pull(string repoPath)
        {
            try
            {
                ExecuteWithRepo(repoPath, repo =>
                {
                    var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
                    Commands.Pull(repo, signature, new PullOptions());
                });
            }
            catch (LibGit2SharpException)
            {
                ExecuteGitCli(repoPath, "pull");
            }
        }

        public void Fetch(string repoPath)
        {
            try
            {
                ExecuteWithRepo(repoPath, repo =>
                {
                    var remote = repo.Network.Remotes["origin"];
                    if (remote != null)
                    {
                        var refSpecs = remote.FetchRefSpecs.Select(x => x.Specification);
                        Commands.Fetch(repo, remote.Name, refSpecs, new FetchOptions(), "");
                    }
                });
            }
            catch (LibGit2SharpException)
            {
                ExecuteGitCli(repoPath, "fetch");
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
            catch (LibGit2SharpException ex)
            {
                // Fallback to CLI if LibGit2Sharp outright fails (e.g., unsupported options)
                ExecuteGitCli(repoPath, $"rebase {targetBranchName}");
            }
        }

        public bool IsRebasing(string repoPath)
        {
            return System.IO.Directory.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-merge")) || 
                   System.IO.Directory.Exists(System.IO.Path.Combine(repoPath, ".git", "rebase-apply"));
        }

        public void ContinueRebase(string repoPath)
        {
            ExecuteGitCli(repoPath, "rebase --continue");
        }

        public void AbortRebase(string repoPath)
        {
            ExecuteGitCli(repoPath, "rebase --abort");
        }

        public void UpdateProject(string repoPath)
        {
            Fetch(repoPath);

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

        // The CLI Fallback Engine
        private void ExecuteGitCli(string repoPath, string arguments)
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    // We route through cmd.exe so it forces a visible terminal window.
                    // The "|| pause" ensures that if Git throws an error (like a merge conflict or bad password),
                    // the window stays open so you can read it before it vanishes!
                    FileName = "cmd.exe",
                    Arguments = $"/c git {arguments} || pause",
                    WorkingDirectory = repoPath,

                    // These must be set so Windows physically draws the terminal window
                    UseShellExecute = true,
                    CreateNoWindow = false
                }
            };

            process.Start();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                // We can't read the error text programmatically anymore because the terminal owns the output,
                // but the user will see it in the pop-up window!
                throw new System.Exception("Git CLI Fallback Failed. See the terminal window for details.");
            }
        }

        // Silent CLI Engine for operations where we want to capture errors to display in-app
        private void ExecuteSilentGitCli(string repoPath, string arguments)
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = repoPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                }
            };

            process.Start();
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new System.Exception(stderr);
            }
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
        
        public IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take)
        {
            return ExecuteWithRepo(repoPath, repo =>
            {
                return repo.Commits.Skip(skip).Take(take).Select(c => new GitCommitItem
                {
                    Sha = c.Sha,
                    ParentShas = c.Parents.Select(p => p.Sha).ToList(),

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
                        IsCurrentRepositoryHead = branch.IsCurrentRepositoryHead
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

        public void CreateBranch(string repoPath, string branchName, bool checkout)
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var newBranch = repo.CreateBranch(branchName, repo.Head.Tip);
                if (checkout)
                {
                    Commands.Checkout(repo, newBranch);
                }
            });
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
                    ExecuteGitCli(repoPath, $"push {remoteName} --delete {remoteBranchName}");
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
            ExecuteSilentGitCli(repoPath, $"stash pop stash@{{{stashIndex}}}");
        }

        public void StashApply(string repoPath, int stashIndex)
        {
            // LibGit2Sharp lacks Apply/Pop natively, fallback to silent CLI
            ExecuteSilentGitCli(repoPath, $"stash apply stash@{{{stashIndex}}}");
        }

        public void Rebase(string repoPath, string targetBranch)
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var branch = repo.Branches[targetBranch];
                if (branch == null) throw new System.Exception($"Branch {targetBranch} not found.");

                var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
                signature ??= new Signature("GitLoom", "gitloom@localhost", System.DateTimeOffset.Now);
                var identity = new Identity(signature.Name, signature.Email);

                repo.Rebase.Start(repo.Head, branch, null, identity, new RebaseOptions());
            });
        }

        public void AbortRebase(string repoPath)
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                repo.Rebase.Abort();
            });
        }

        public void ContinueRebase(string repoPath)
        {
            ExecuteWithRepo(repoPath, repo =>
            {
                var signature = repo.Config.BuildSignature(System.DateTimeOffset.Now);
                signature ??= new Signature("GitLoom", "gitloom@localhost", System.DateTimeOffset.Now);
                var identity = new Identity(signature.Name, signature.Email);

                repo.Rebase.Continue(identity, new RebaseOptions());
            });
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
            ExecuteSilentGitCli(repoPath, $"worktree add \"{worktreePath}\" \"{branchName}\"");
        }

        public void RemoveWorktree(string repoPath, string worktreePath)
        {
            ExecuteSilentGitCli(repoPath, $"worktree remove \"{worktreePath}\"");
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
    }