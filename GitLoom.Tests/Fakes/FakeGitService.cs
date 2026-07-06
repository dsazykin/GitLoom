using System;
using System.Collections.Generic;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using LibGit2Sharp;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests.Fakes;

/// <summary>
/// Hand-rolled configurable <see cref="IGitService"/> fake (TI-00 pattern — no mocking library,
/// keeping with the zero-container philosophy). Members used by a test are backed by settable
/// delegates; everything else throws <see cref="NotSupportedException"/> so an unstubbed call is
/// a loud test error rather than a silent default.
/// </summary>
public sealed class FakeGitService : IGitService
{
    /// <summary>Stub for <see cref="GetBlame"/>. Args: (repoPath, path, startingSha).</summary>
    public Func<string, string, string?, IReadOnlyList<BlameLine>>? GetBlameImpl { get; set; }

    public Action<string>? InvalidateBlameCacheImpl { get; set; }

    public IReadOnlyList<BlameLine> GetBlame(string repoPath, string path, string? startingSha = null)
        => (GetBlameImpl ?? throw new NotSupportedException("GetBlameImpl not set"))(repoPath, path, startingSha);

    public void InvalidateBlameCache(string repoPath) => InvalidateBlameCacheImpl?.Invoke(repoPath);

    // File history (T-12). Args mirror the interface; unstubbed calls throw.
    public Func<string, string, IReadOnlyList<FileVersion>>? GetFileHistoryImpl { get; set; }
    public Func<string, string, string, string>? GetFileAtCommitImpl { get; set; }
    public Func<string, string, string, string, string>? GetFileDiffBetweenCommitsImpl { get; set; }

    public IReadOnlyList<FileVersion> GetFileHistory(string repoPath, string path)
        => (GetFileHistoryImpl ?? throw new NotSupportedException("GetFileHistoryImpl not set"))(repoPath, path);

    public string GetFileAtCommit(string repoPath, string sha, string path)
        => (GetFileAtCommitImpl ?? throw new NotSupportedException("GetFileAtCommitImpl not set"))(repoPath, sha, path);

    public string GetFileDiffBetweenCommits(string repoPath, string olderSha, string newerSha, string path)
        => (GetFileDiffBetweenCommitsImpl ?? throw new NotSupportedException("GetFileDiffBetweenCommitsImpl not set"))(repoPath, olderSha, newerSha, path);

    // ---- Everything else: not stubbed unless a test needs it.
    private static T Nope<T>() => throw new NotSupportedException();
    private static void Nope() => throw new NotSupportedException();

    public bool IsGitRepository(string path) => Nope<bool>();
    public List<GitFileStatus> GetRepositoryStatus(string path) => Nope<List<GitFileStatus>>();
    public void ExecuteWithRepo(string path, Action<Repository> action) => Nope();
    public T ExecuteWithRepo<T>(string path, Func<Repository, T> func) => Nope<T>();
    public void StageFile(string repoPath, string filePath) => Nope();
    public void UnstageFile(string repoPath, string filePath) => Nope();
    public void StageFiles(string repoPath, IEnumerable<string> filePaths) => Nope();
    public void UnstageFiles(string repoPath, IEnumerable<string> filePaths) => Nope();
    public void DiscardChanges(string repoPath, IEnumerable<string> filePaths) => Nope();
    public void StageHunk(string repoPath, string patch) => Nope();
    public void UnstageHunk(string repoPath, string patch) => Nope();
    public void DiscardHunk(string repoPath, string patch) => Nope();
    public string GetFileDiff(string repoPath, string filePath, bool isStaged) => Nope<string>();
    public void Commit(string repoPath, string message) => Nope();
    public void Push(string repoPath) => Nope();
    public void Pull(string repoPath) => Nope();
    public void Pull(string repoPath, PullStrategy strategy) => Nope();
    public void Fetch(string repoPath, bool prune = false) => Nope();
    public void UpdateProject(string repoPath) => Nope();
    public IReadOnlyList<GitRemoteItem> GetRemotes(string repoPath) => Nope<IReadOnlyList<GitRemoteItem>>();
    public void AddRemote(string repoPath, string name, string url) => Nope();
    public void RemoveRemote(string repoPath, string name) => Nope();
    public void RenameRemote(string repoPath, string oldName, string newName) => Nope();
    public void SetRemoteUrl(string repoPath, string name, string url) => Nope();
    public string GetDefaultRemoteName(string repoPath) => Nope<string>();
    public void Fetch(string repoPath, string remoteName, bool prune = false) => Nope();
    public void PushForceWithLease(string repoPath, string remoteName, string branchName) => Nope();
    public void PushTags(string repoPath, string remoteName) => Nope();
    public void PushSetUpstream(string repoPath, string remoteName, string branchName) => Nope();
    public void PushWithCredentials(string repoPath, string username, string password) => Nope();
    public void PullWithCredentials(string repoPath, string username, string password) => Nope();
    public void Rebase(string repoPath, string targetBranchName) => Nope();
    public void Merge(string repoPath, string sourceBranchName) => Nope();
    public bool IsMergeInProgress(string repoPath) => Nope<bool>();
    public string GetMergeMessage(string repoPath) => Nope<string>();
    public IReadOnlyList<ConflictedFile> GetConflicts(string repoPath) => Nope<IReadOnlyList<ConflictedFile>>();
    public (string BaseText, string OursText, string TheirsText) GetConflictBlobs(string repoPath, string path) => Nope<(string, string, string)>();
    public void ResolveConflict(string repoPath, string path, string mergedContent) => Nope();
    public bool HasUnresolvedConflicts(string repoPath) => Nope<bool>();
    public void ResolveFileWithSide(string repoPath, string path, ConflictSide side) => Nope();
    public void RemoveFileFromMerge(string repoPath, string path) => Nope();
    public CurrentOperation GetCurrentOperation(string repoPath) => Nope<CurrentOperation>();
    public void AbortMerge(string repoPath) => Nope();
    public bool IsRebasing(string repoPath) => Nope<bool>();
    public void ContinueRebase(string repoPath) => Nope();
    public void AbortRebase(string repoPath) => Nope();
    public (int? Ahead, int? Behind) GetAheadBehind(string repoPath) => Nope<(int?, int?)>();
    public IEnumerable<GitCommitItem> GetRecentCommits(string repoPath, int skip, int take, CommitSearchFilter? filter = null) => Nope<IEnumerable<GitCommitItem>>();
    public IEnumerable<GitBranchItem> GetBranches(string repoPath) => Nope<IEnumerable<GitBranchItem>>();
    public void CheckoutBranch(string repoPath, string branchName) => Nope();
    public void CreateBranch(string repoPath, string branchName, string baseBranchName, bool checkout) => Nope();
    public void RenameBranch(string repoPath, string oldName, string newName) => Nope();
    public void PushBranch(string repoPath, string branchName) => Nope();
    public void DeleteBranch(string repoPath, string branchName, bool force = false) => Nope();
    public void StashChanges(string repoPath, string message) => Nope();
    public bool HasUncommittedChanges(string repoPath) => Nope<bool>();
    public IEnumerable<GitTagItem> GetTags(string repoPath) => Nope<IEnumerable<GitTagItem>>();
    public void CreateTag(string repoPath, string name, string targetSha, string? message) => Nope();
    public void DeleteTag(string repoPath, string name) => Nope();
    public void PushTag(string repoPath, string remoteName, string name) => Nope();
    public void DeleteRemoteTag(string repoPath, string remoteName, string name) => Nope();
    public void CheckoutTag(string repoPath, string name) => Nope();
    public IEnumerable<GitStashItem> GetStashes(string repoPath) => Nope<IEnumerable<GitStashItem>>();
    public void StashPush(string repoPath, string message) => Nope();
    public void StashDrop(string repoPath, int stashIndex) => Nope();
    public void StashPop(string repoPath, int stashIndex) => Nope();
    public void StashApply(string repoPath, int stashIndex) => Nope();
    public IReadOnlyList<WorktreeItem> ListWorktrees(string repoPath) => Nope<IReadOnlyList<WorktreeItem>>();
    public void AddWorktree(string repoPath, string worktreePath, string branchName, bool createBranch) => Nope();
    public void RemoveWorktree(string repoPath, string worktreePath, bool force) => Nope();
    public void PruneWorktrees(string repoPath) => Nope();
    public string GetDiffAgainstCommit(string repoPath, string commitSha, string? filePath = null) => Nope<string>();
    public string GetBranchDiffAgainstWorkingTree(string repoPath, string branchName) => Nope<string>();
    public IEnumerable<string> GetCommitModifiedFiles(string repoPath, string commitSha) => Nope<IEnumerable<string>>();
    public IEnumerable<string> GetBranchesContainingCommit(string repoPath, string commitSha) => Nope<IEnumerable<string>>();
    public IEnumerable<string> GetAuthors(string repoPath) => Nope<IEnumerable<string>>();
    public IEnumerable<string> GetRepositoryPaths(string repoPath) => Nope<IEnumerable<string>>();
    public void CheckoutRevision(string repoPath, string commitSha) => Nope();
    public void ResetToCommit(string repoPath, string commitSha, ResetMode mode) => Nope();
    public void RevertCommit(string repoPath, string commitSha) => Nope();
    public void AmendCommitMessage(string repoPath, string commitSha, string newMessage) => Nope();
    public void CherryPick(string repoPath, string commitSha) => Nope();
    public GitHeadState GetHeadState(string repoPath) => Nope<GitHeadState>();
    public void CreateBranchAt(string repoPath, string branchName, string commitSha, bool checkout) => Nope();
}
