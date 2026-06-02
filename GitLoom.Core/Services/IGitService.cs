using System;
using LibGit2Sharp;
using System.Collections.Generic;
using GitLoom.Core.Models;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Core.Services;

public interface IGitService
{
    /// <summary>
    /// Checks if the specified directory is a valid Git repository.
    /// </summary>
    bool IsGitRepository(string path);
    
    /// <summary>
    /// Retrieves the current working directory and index status of the repository.
    /// </summary>
    List<GitFileStatus> GetRepositoryStatus(string path);

    /// <summary>
    /// Executes a Git command using LibGit2Sharp, managing the native handle lifecycle strictly.
    /// </summary>
    void ExecuteWithRepo(string path, Action<Repository> action);

    /// <summary>
    /// Executes a Git command using LibGit2Sharp and returns a result, managing the native handle lifecycle strictly.
        /// </summary>
        T ExecuteWithRepo<T>(string path, Func<Repository, T> func);
}