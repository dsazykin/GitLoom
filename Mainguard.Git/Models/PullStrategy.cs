namespace Mainguard.Git.Models;

/// <summary>
/// How a pull should integrate upstream commits into the current branch.
/// </summary>
public enum PullStrategy
{
    /// <summary>Fast-forward when possible, otherwise create a merge commit.</summary>
    Default,

    /// <summary>Only fast-forward; fail if the branches have diverged.</summary>
    FastForwardOnly,

    /// <summary>Fetch, then rebase the current branch onto its upstream tip.</summary>
    Rebase
}
