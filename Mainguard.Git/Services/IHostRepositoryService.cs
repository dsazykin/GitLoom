using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic "list my repositories" service (P2-48). Resolves a host's stored token and dispatches
/// to the matching per-host provider (GitHub + GitLab implemented; Bitbucket/Azure DevOps stubs),
/// returning the host-agnostic <see cref="RemoteRepository"/> model — a host-specific JSON shape never
/// leaks out of a provider (G-10). The Clone Dashboard consumes only this surface, so it works for any
/// host the user is signed into. Mirrors the T-23 <c>IPullRequestService</c>/provider structure.
///
/// <para>SECURITY (G-4): a token resolved here travels only in the provider's <c>Authorization</c>
/// header — never a URL query, argv, log, or exception message.</para>
/// </summary>
public interface IHostRepositoryService
{
    /// <summary>True when this host has an implemented provider AND a stored token — i.e. its repos are listable.</summary>
    bool IsSupported(string host, HostKind kind);

    /// <summary>
    /// Lists the signed-in account's repositories for a host (all memberships, most-recently-updated
    /// first, capped at 100 — no paging). Throws a typed error when no provider/token is available.
    /// </summary>
    Task<IReadOnlyList<RemoteRepository>> ListMyRepositoriesAsync(string host, HostKind kind, CancellationToken ct);
}
