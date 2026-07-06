using System;
using System.Collections.Generic;
using GitLoom.Core.Safety;
using LibGit2Sharp;

namespace GitLoom.Core.Services;

/// <summary>
/// T-30 pre-commit scanner over the staged index. Enumerates the added/modified/renamed entries in
/// the index vs HEAD via <see cref="IGitService.ExecuteWithRepo"/>, reads each staged blob (skipping
/// the text read for binaries and for anything over the size cap so a huge blob never gets slurped
/// into memory), and feeds the pure <see cref="PreCommitScanEngine"/>. Deletions are ignored (nothing
/// to scan). No network, no CLI.
/// </summary>
public sealed class PreCommitScanner : IPreCommitScanner
{
    private readonly IGitService _git;

    public PreCommitScanner(IGitService git) => _git = git;

    public IReadOnlyList<PreCommitFinding> ScanStaged(string repoPath)
        => ScanStaged(repoPath, new PreCommitScanOptions());

    public IReadOnlyList<PreCommitFinding> ScanStaged(string repoPath, PreCommitScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var entries = _git.ExecuteWithRepo(repoPath, repo =>
        {
            var result = new List<(string Path, string Content, bool IsBinary, long SizeBytes)>();

            // Staged change set = index compared against HEAD's tree (null tree for an unborn HEAD).
            Tree? headTree = repo.Head.Tip?.Tree;
            var changes = repo.Diff.Compare<TreeChanges>(headTree, DiffTargets.Index);

            foreach (var change in changes)
            {
                if (change.Status is not (ChangeKind.Added or ChangeKind.Modified or ChangeKind.Renamed))
                {
                    continue; // deleted / unmodified — nothing staged to scan
                }

                var indexEntry = repo.Index[change.Path];
                if (indexEntry is null) continue;

                if (repo.Lookup<Blob>(indexEntry.Id) is not Blob blob) continue;

                long size = blob.Size;
                bool isBinary = blob.IsBinary; // libgit2 sniffs only the head of the blob — cheap

                // Size check BEFORE reading text: never decode a blob larger than the cap. It is
                // still reported (LargeFile) purely from its size by the engine.
                string content = string.Empty;
                if (!isBinary && size <= options.MaxFileBytes)
                {
                    content = blob.GetContentText();
                }

                result.Add((change.Path, content, isBinary, size));
            }

            return result;
        });

        return PreCommitScanEngine.Scan(entries, options);
    }
}
