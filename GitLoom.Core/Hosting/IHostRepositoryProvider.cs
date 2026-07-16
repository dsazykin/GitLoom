using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Models;

namespace GitLoom.Core.Hosting;

/// <summary>
/// Per-host "list my repositories" adapter (P2-48). One concrete provider per host family speaks the
/// host's REST dialect and maps its JSON to the host-agnostic <see cref="RemoteRepository"/> model
/// (G-10 — the host JSON shape never leaves the provider). The token is supplied per call and lives
/// only in the provider's <c>Authorization</c> header — never a URL, argv, log, or exception message.
///
/// <para><see cref="IsImplemented"/> is false for the Bitbucket/Azure DevOps stubs so the dispatch table
/// stays complete (adding a real provider is additive) while the service reports those hosts as
/// unsupported until their live flow lands — mirroring the T-23 pull-request provider pattern.</para>
/// </summary>
internal interface IHostRepositoryProvider
{
    /// <summary>False for stub providers whose live flow isn't built yet; the service treats them as unsupported.</summary>
    bool IsImplemented { get; }

    /// <summary>
    /// Lists the signed-in account's repositories (all memberships, most-recently-updated first, capped
    /// at 100 — no paging), mapped to the host-agnostic model. <paramref name="host"/> lets a self-hosted
    /// instance address its own origin.
    /// </summary>
    Task<IReadOnlyList<RemoteRepository>> ListMyRepositoriesAsync(string host, string token, CancellationToken ct);
}
