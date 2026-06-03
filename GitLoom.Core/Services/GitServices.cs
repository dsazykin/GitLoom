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
                ExecuteWithRepo(repoPath, repo =>
                {
                    var branch = repo.Head;
                    if (branch.TrackedBranch == null) throw new System.Exception("No upstream branch configured.");

                    // Attempt ultra-fast native C push (works for unauthenticated or basic HTTPS)
                    repo.Network.Push(branch, new PushOptions());
                });
            }
            catch (LibGit2SharpException)
            {
                // If it crashes due to SSH or Credentials, fallback to the native terminal Git!
                ExecuteGitCli(repoPath, "push");
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
                // If it crashes due to SSH or Credentials, fallback to the native terminal Git!
                ExecuteGitCli(repoPath, "pull");
            }
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
}