using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Git.Hosting;

/// <summary>
/// GitHub "list my repositories" provider (P2-48, REST v3). Lists <c>/user/repos</c> across all
/// affiliations (owner, collaborator, org member — GitHub's default), most-recently-updated first,
/// capped at 100 (no paging), over the shared audited <see cref="GitHubApiClient"/> transport. Maps
/// GitHub's JSON to the host-agnostic <see cref="RemoteRepository"/> (G-10). Preserves the exact query
/// the Clone Dashboard used before it was generalized (<c>sort=updated&amp;per_page=100</c>).
///
/// <para>SECURITY (G-4): the token is written only to the transport's <c>Authorization: Bearer</c>
/// header — never a URL, argv, log, or exception message.</para>
/// </summary>
internal sealed class GitHubRepositoryProvider : IHostRepositoryProvider
{
    private readonly GitHubApiClient _api;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubRepositoryProvider(HttpClient http, string apiBase = "https://api.github.com")
        => _api = new GitHubApiClient(http, apiBase);

    public async Task<IReadOnlyList<RemoteRepository>> ListMyRepositoriesAsync(string host, string token, CancellationToken ct)
    {
        var url = $"{_api.ApiBase}/user/repos?sort=updated&per_page=100";
        var json = await _api.SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dtos = GitHubApiClient.Deserialize<List<RepoDto>>(json) ?? new();
        return dtos.Select(d => Map(d, host)).ToList();
    }

    private static RemoteRepository Map(RepoDto d, string host) => new()
    {
        Kind = HostKind.GitHub,
        Host = host,
        Name = d.Name ?? "",
        FullName = d.FullName ?? "",
        CloneUrl = d.CloneUrl ?? "",
        HtmlUrl = d.HtmlUrl ?? "",
        Description = d.Description,
        IsPrivate = d.Private,
        UpdatedAt = d.UpdatedAt ?? "",
    };

    // ---- GitHub JSON shape (never leaves this file) --------------------------------------------
    private sealed class RepoDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
        [JsonPropertyName("private")] public bool Private { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("clone_url")] public string? CloneUrl { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
    }
}
