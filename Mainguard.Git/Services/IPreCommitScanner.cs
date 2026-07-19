using System.Collections.Generic;
using Mainguard.Git.Safety;

namespace Mainguard.Git.Services;

/// <summary>
/// T-30 pre-commit safety scanner. Reads the STAGED tree of a repo, decodes the added/modified
/// blobs, and runs the pure <see cref="PreCommitScanEngine"/> over them. No network; all git access
/// goes through <see cref="IGitService.ExecuteWithRepo"/>.
/// </summary>
public interface IPreCommitScanner
{
    /// <summary>Scan the staged tree with default thresholds.</summary>
    IReadOnlyList<PreCommitFinding> ScanStaged(string repoPath);

    /// <summary>Scan the staged tree with caller-supplied thresholds (from UserPreferences).</summary>
    IReadOnlyList<PreCommitFinding> ScanStaged(string repoPath, PreCommitScanOptions options);
}
