namespace Mainguard.Git.Models;

/// <summary>
/// The Git hosting provider a remote points at, detected from its URL. Drives
/// per-host credential lookup and the username convention used for token auth.
/// </summary>
public enum HostKind
{
    Unknown,
    GitHub,
    GitLab,
    Bitbucket,
    AzureDevOps
}
