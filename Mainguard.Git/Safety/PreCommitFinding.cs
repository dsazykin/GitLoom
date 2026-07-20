namespace Mainguard.Git.Safety;

/// <summary>
/// How serious a <see cref="PreCommitFinding"/> is. <see cref="Blocker"/> (a secret or a merge
/// marker) gates the commit behind an explicit "Commit anyway"; <see cref="Warning"/> is advisory;
/// <see cref="Info"/> is informational only.
/// </summary>
public enum FindingSeverity
{
    Info,
    Warning,
    Blocker
}

/// <summary>What kind of problem a finding represents.</summary>
public enum FindingKind
{
    Secret,
    LargeFile,
    MergeMarker,
    DebugLeftover,
    ManyFiles,
    Other
}

/// <summary>
/// One issue the T-30 pre-commit scanner found in the staged change. Plain data — produced by the
/// pure <see cref="PreCommitScanEngine"/> so the finding set is unit-pinned.
/// <para>
/// INVARIANT: <see cref="Message"/> is human text built from the rule name + location only and MUST
/// NEVER contain the matched secret value. The value is never captured into any field here.
/// </para>
/// </summary>
public sealed class PreCommitFinding
{
    public FindingKind Kind { get; init; }
    public FindingSeverity Severity { get; init; }

    /// <summary>Repo-relative path of the staged file the finding is in (empty for repo-wide findings like ManyFiles).</summary>
    public string Path { get; init; } = "";

    /// <summary>1-based line number for line-scoped findings (secrets, merge markers); null otherwise.</summary>
    public int? Line { get; init; }

    /// <summary>Human message: rule name + location only — never echoes the matched secret.</summary>
    public string Message { get; init; } = "";

    /// <summary>Stable rule id, e.g. "aws-access-key-id", "merge-marker", "large-file".</summary>
    public string Rule { get; init; } = "";
}
