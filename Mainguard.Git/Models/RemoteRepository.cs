namespace Mainguard.Git.Models;

/// <summary>
/// A repository listed from a hosting provider's "my repositories" API (P2-48), in a
/// host-agnostic shape. Each provider maps its own JSON (GitHub <c>/user/repos</c>,
/// GitLab <c>/api/v4/projects</c>, …) onto this model so a host-specific JSON shape
/// never leaks out of the provider (G-10) — the Clone Dashboard binds only to this.
/// </summary>
public class RemoteRepository
{
    /// <summary>Which host family this repo came from (drives the per-host token/clone credential).</summary>
    public HostKind Kind { get; set; } = HostKind.Unknown;

    /// <summary>The host this repo lives on (e.g. <c>github.com</c>, <c>gitlab.com</c>, a self-hosted origin).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Short repository name (GitHub <c>name</c> / GitLab project <c>path</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Owner-qualified name (GitHub <c>full_name</c> / GitLab <c>path_with_namespace</c>).</summary>
    public string FullName { get; set; } = string.Empty;

    /// <summary>HTTPS clone URL handed to <c>ICloneService</c> (GitHub <c>clone_url</c> / GitLab <c>http_url_to_repo</c>).</summary>
    public string CloneUrl { get; set; } = string.Empty;

    /// <summary>Web URL for the repository (GitHub <c>html_url</c> / GitLab <c>web_url</c>).</summary>
    public string HtmlUrl { get; set; } = string.Empty;

    /// <summary>Optional description shown on the repo card.</summary>
    public string? Description { get; set; }

    /// <summary>True when the repo is private/internal (GitHub <c>private</c> / GitLab <c>visibility != public</c>).</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Last-updated timestamp as the host returned it (ISO&nbsp;8601); used only to sort "most recent".</summary>
    public string UpdatedAt { get; set; } = string.Empty;

    /// <summary>UI-only: set by the dashboard when a repo with this name is already cloned locally.</summary>
    public bool IsAddedLocally { get; set; }
}
