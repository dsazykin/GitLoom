using System;
using LibGit2Sharp;

namespace GitLoom.Core.Services;

public interface IGitService
{
    /// <summary>
    /// Checks if the specified directory is a valid Git repository.
    /// </summary>
    bool IsGitRepository(string path);

    /// <summary>
    /// Executes a Git command using LibGit2Sharp, managing the native handle lifecycle strictly.
    /// </summary>
    void ExecuteWithRepo(string path, Action<Repository> action);

    /// <summary>
    /// Executes a Git command using LibGit2Sharp and returns a result, managing the native handle lifecycle strictly.
        /// </summary>
        T ExecuteWithRepo<T>(string path, Func<Repository, T> func);
}