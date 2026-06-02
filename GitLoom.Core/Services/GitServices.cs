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
}