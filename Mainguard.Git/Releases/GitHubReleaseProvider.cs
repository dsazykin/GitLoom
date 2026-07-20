using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Hosting;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug

namespace Mainguard.Git.Releases;

/// <summary>
/// GitHub releases provider (T-28, REST v3). Speaks GitHub's releases dialect over the shared
/// <see cref="GitHubApiClient"/> transport — the same audited send + typed-error + redaction path the
/// T-23/T-24/T-26 providers use (no second HTTP/token path). Tests wrap a fixture
/// <see cref="HttpMessageHandler"/> so parsing runs offline.
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the transport's per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message; host error text is
/// scrubbed of the token by the shared client.</para>
/// </summary>
internal sealed class GitHubReleaseProvider : IReleaseProvider
{
    private readonly GitHubApiClient _api;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubReleaseProvider(HttpClient http, string apiBase = "https://api.github.com")
        => _api = new GitHubApiClient(http, apiBase);

    // TODO(T-28 human-review): live release matrix — the endpoints below are fully built and fixture-tested,
    // but the real list/create round trip against a GitHub account (incl. publishing a draft release) is
    // host-account-gated and deferred to the manual matrix (mirrors T-23/T-24/T-26/T-27's live deferral).

    public async Task<IReadOnlyList<ReleaseItem>> ListAsync(RepoSlug repo, string token, CancellationToken ct)
    {
        var url = $"{Base(repo)}/releases?per_page=100";
        var json = await _api.SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dtos = GitHubApiClient.Deserialize<List<ReleaseDto>>(json) ?? new();
        return dtos.Select(MapItem).ToList();
    }

    public async Task<ReleaseItem> CreateAsync(RepoSlug repo, string token, CreateRelease request, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new CreateDto
        {
            TagName = request.TagName,
            TargetCommitish = string.IsNullOrWhiteSpace(request.TargetCommitish) ? null : request.TargetCommitish,
            Name = request.Name,
            Body = request.Body,
            Draft = request.IsDraft,
            Prerelease = request.IsPrerelease,
        });
        var json = await _api.SendAsync(HttpMethod.Post, $"{Base(repo)}/releases", token, payload, ct);
        var dto = GitHubApiClient.Deserialize<ReleaseDto>(json)
            ?? throw new GitOperationException("GitHub returned an empty create-release response.");
        return MapItem(dto);
    }

    // ---- JSON → models -------------------------------------------------------------------------

    private string Base(RepoSlug repo) =>
        $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}";

    private static ReleaseItem MapItem(ReleaseDto d) => new()
    {
        Id = d.Id,
        TagName = d.TagName ?? "",
        Name = string.IsNullOrEmpty(d.Name) ? (d.TagName ?? "") : d.Name!,
        Body = d.Body ?? "",
        IsDraft = d.Draft,
        IsPrerelease = d.Prerelease,
        Author = d.Author?.Login ?? "",
        PublishedAt = ParseDate(d.PublishedAt),
        Url = d.HtmlUrl ?? "",
    };

    private static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : (DateTimeOffset?)null;

    // ---- GitHub JSON shapes (never leave this file) --------------------------------------------

    private sealed class ReleaseDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
        [JsonPropertyName("author")] public UserDto? Author { get; set; }
    }

    private sealed class UserDto { [JsonPropertyName("login")] public string? Login { get; set; } }

    private sealed class CreateDto
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("target_commitish")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? TargetCommitish { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }
}
