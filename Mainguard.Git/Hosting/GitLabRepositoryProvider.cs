using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Git.Hosting;

/// <summary>
/// GitLab "list my repositories" provider (P2-48, REST v4). Lists <c>/projects?membership=true</c>
/// across all memberships, ordered <c>last_activity_at</c> (most-recent first), capped at 100 (no
/// paging), over the shared audited <see cref="GitLabApiClient"/> transport. Maps GitLab's project JSON
/// to the host-agnostic <see cref="RemoteRepository"/> (G-10). Works for gitlab.com and self-hosted
/// GitLab — the API base is derived from the host, so a self-hosted instance queries its own origin.
///
/// <para>SECURITY (G-4): the token is written only to the transport's <c>Authorization: Bearer</c>
/// header — never a URL, argv, log, or exception message.</para>
/// </summary>
internal sealed class GitLabRepositoryProvider : IHostRepositoryProvider
{
    private readonly HttpClient _http;
    private readonly string? _apiBaseOverride;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">Optional API-root override (tests); otherwise derived from the host per call.</param>
    public GitLabRepositoryProvider(HttpClient http, string? apiBase = null)
    {
        _http = http;
        _apiBaseOverride = apiBase;
    }

    public async Task<IReadOnlyList<RemoteRepository>> ListMyRepositoriesAsync(string host, string token, CancellationToken ct)
    {
        var apiBase = _apiBaseOverride ?? GitLabApiClient.ApiBaseForHost(host);
        var api = new GitLabApiClient(_http, apiBase);
        var url = $"{api.ApiBase}/projects?membership=true&per_page=100&order_by=last_activity_at";
        var json = await api.SendAsync(HttpMethod.Get, url, token, ct);
        var dtos = GitLabApiClient.Deserialize<List<ProjectDto>>(json) ?? new();
        return dtos.Select(d => Map(d, host)).ToList();
    }

    private static RemoteRepository Map(ProjectDto d, string host) => new()
    {
        Kind = HostKind.GitLab,
        Host = host,
        Name = d.Path ?? "",
        FullName = d.PathWithNamespace ?? "",
        CloneUrl = d.HttpUrlToRepo ?? "",
        HtmlUrl = d.WebUrl ?? "",
        Description = string.IsNullOrEmpty(d.Description) ? null : d.Description,
        // GitLab visibility is one of public/internal/private; anything but "public" is non-public.
        IsPrivate = !string.Equals(d.Visibility, "public", StringComparison.OrdinalIgnoreCase),
        UpdatedAt = d.LastActivityAt ?? "",
    };

    // ---- GitLab JSON shape (never leaves this file) --------------------------------------------
    private sealed class ProjectDto
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("path_with_namespace")] public string? PathWithNamespace { get; set; }
        [JsonPropertyName("http_url_to_repo")] public string? HttpUrlToRepo { get; set; }
        [JsonPropertyName("web_url")] public string? WebUrl { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("visibility")] public string? Visibility { get; set; }
        [JsonPropertyName("last_activity_at")] public string? LastActivityAt { get; set; }
    }
}
