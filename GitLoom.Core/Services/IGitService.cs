using System;
using System.Collections.Generic;
using GitLoom.Core.Models;
using LibGit2Sharp;
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

    void StageFile(string repoPath, string filePath);
    void UnstageFile(string repoPath, string filePath);

    void StageFiles(string repoPath, IEnumerable<string> filePaths);
    void UnstageFiles(string repoPath, IEnumerable<string> filePaths);
    void DiscardChanges(string repoPath, IEnumerable<string> filePaths);

    /// <summary>Stages a subset of changes described by a unified-diff patch (git apply --cached).</summary>
    void StageHunk(string repoPath, string patch);
    /// <summary>Unstages a subset of changes described by a unified-diff patch (git apply --cached --reverse).</summary>
    void UnstageHunk(string repoPath, string patch);
    /// <summary>Discards a subset of working-tree changes described by a unified-diff patch (git apply --reverse).</summary>
    void DiscardHunk(string repoPath, string patch);

    string GetFileDiff(string repoPath, string filePath, bool isStaged);

    void Commit(string repoPath, string message);

    void Push(string repoPath);
    void Pull(string repoPath);
    void Pull(string repoPath, PullStrategy strategy);
    void Fetch(string repoPath, bool prune = false);
    void UpdateProject(string repoPath);

    void PushWithCredentials(string repoPath, string username, string password);
    void PullWithCredentials(string repoPath, string username, string password);

    void Rebase(string repoPath, string targetBranchName);
    void Merge(string repoPath, string sourceBranchName);
    bool IsMergeInProgress(string repoPath);
    string GetMergeMessage(string repoPath);

    // Conflict resolution — merge index stages (repo.Index.Conflicts), never working-tree markers.
    IReadOnlyList<ConflictedFile> GetConflicts(string repoPath);
    (string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path);
    void ResolveConflict(string repoPath, string path, string mergedContent);
    bool HasUnresolvedConflicts(string repoPath);

    bool IsRebasing(string repoPath);
    void ContinueRebase(string repoPath);
    void AbortRebase(string repoPath);

    (int? Ahead, int? Behind) GetAheadBehind(string repoPath);

    IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take, CommitSearchFilter? filter = null);

    IEnumerable<GitBranchItem> GetBranches(string repoPath);
    void CheckoutBranch(string repoPath, string branchName);
    void CreateBranch(string repoPath, string branchName, string baseBranchName, bool checkout);
    void RenameBranch(string repoPath, string oldName, string newName);
    void PushBranch(string repoPath, string branchName);
    void DeleteBranch(string repoPath, string branchName, bool force = false);
    void StashChanges(string repoPath, string message);
    bool HasUncommittedChanges(string repoPath);

    IEnumerable<GitStashItem> GetStashes(string repoPath);
    void StashPush(string repoPath, string message);
    void StashDrop(string repoPath, int stashIndex);
    void StashPop(string repoPath, int stashIndex);
    void StashApply(string repoPath, int stashIndex);

    IEnumerable<string> ListWorktrees(string repoPath);
    void AddWorktree(string repoPath, string worktreePath, string branchName);
    void RemoveWorktree(string repoPath, string worktreePath);

    string GetDiffAgainstCommit(string repoPath, string commitSha, string filePath);

    string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName);

    IEnumerable<string> GetCommitModifiedFiles(string repoPath, string commitSha);
    IEnumerable<string> GetBranchesContainingCommit(string repoPath, string commitSha);

    IEnumerable<string> GetAuthors(string repoPath);
    IEnumerable<string> GetRepositoryPaths(string repoPath);

    void CheckoutRevision(string repoPath, string commitSha);
    void ResetToCommit(string repoPath, string commitSha, LibGit2Sharp.ResetMode mode);
    void RevertCommit(string repoPath, string commitSha);
    void AmendCommitMessage(string repoPath, string commitSha, string newMessage);
    void CherryPick(string repoPath, string commitSha);
}
