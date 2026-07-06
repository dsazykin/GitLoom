using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Hosting;
using GitLoom.Core.Models;
using GitLoom.Core.Services; // shared RepoSlug

namespace GitLoom.Core.PullRequests;

/// <summary>
/// GitHub pull-request provider (T-23, REST v3). Speaks GitHub's PR dialect over the shared
/// <see cref="GitHubApiClient"/> transport (one audited send + error-mapping path, reused by the T-24
/// issue provider); tests wrap a fixture <see cref="HttpMessageHandler"/> so parsing runs offline.
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the transport's per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message. Any host error
/// text folded into a thrown exception is first scrubbed of the token by the shared client.</para>
/// </summary>
internal sealed class GitHubPullRequestProvider : IPullRequestProvider
{
    private readonly GitHubApiClient _api;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubPullRequestProvider(HttpClient http, string apiBase = "https://api.github.com")
        => _api = new GitHubApiClient(http, apiBase);

    // TODO(T-23 human-review): live PR matrix — the endpoints below are fully built and fixture-tested,
    // but the real create/list/merge/close round trip against a GitHub account is host-account-gated and
    // deferred to the manual matrix (mirrors T-14's live-auth deferral).

    public async Task<IReadOnlyList<PullRequestItem>> ListAsync(RepoSlug repo, string token, PullRequestState filter, CancellationToken ct)
    {
        var state = filter switch
        {
            PullRequestState.Closed => "closed",
            PullRequestState.Merged => "all",
            _ => "open",           // Open / Draft are both "open" on the host
        };
        var url = $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}/pulls?state={state}&per_page=100";
        var json = await _api.SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dtos = GitHubApiClient.Deserialize<List<PullDto>>(json) ?? new();

        IEnumerable<PullDto> selected = dtos;
        if (filter == PullRequestState.Merged)
            selected = dtos.Where(d => d.MergedAt is not null);
        else if (filter == PullRequestState.Draft)
            selected = dtos.Where(d => d.Draft);

        return selected.Select(MapItem).ToList();
    }

    public async Task<PullRequestDetail> GetAsync(RepoSlug repo, string token, int number, CancellationToken ct)
    {
        var url = $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}/pulls/{number}";
        var json = await _api.SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dto = GitHubApiClient.Deserialize<PullDto>(json) ?? throw new GitOperationException("GitHub returned an empty pull request response.");
        return new PullRequestDetail
        {
            Summary = MapItem(dto),
            Body = dto.Body ?? "",
            Mergeable = dto.Mergeable ?? false,
            Reviewers = (dto.RequestedReviewers ?? new()).Select(r => r.Login ?? "").Where(s => s.Length > 0).ToList(),
        };
    }

    public async Task<PullRequestItem> CreateAsync(RepoSlug repo, string token, CreatePullRequest request, CancellationToken ct)
    {
        var url = $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}/pulls";
        var payload = JsonSerializer.Serialize(new CreateDto
        {
            Title = request.Title,
            Head = request.SourceBranch,
            Base = request.TargetBranch,
            Body = request.Body,
            Draft = request.IsDraft,
        });
        var json = await _api.SendAsync(HttpMethod.Post, url, token, payload, ct);
        var dto = GitHubApiClient.Deserialize<PullDto>(json) ?? throw new GitOperationException("GitHub returned an empty create-pull-request response.");
        return MapItem(dto);
    }

    public async Task<PullRequestItem> MergeAsync(RepoSlug repo, string token, int number, PullRequestMergeMethod method, CancellationToken ct)
    {
        var url = $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}/pulls/{number}/merge";
        var payload = JsonSerializer.Serialize(new MergeDto
        {
            MergeMethod = method switch
            {
                PullRequestMergeMethod.Squash => "squash",
                PullRequestMergeMethod.Rebase => "rebase",
                _ => "merge",
            },
        });
        var json = await _api.SendAsync(HttpMethod.Put, url, token, payload, ct);
        var result = GitHubApiClient.Deserialize<MergeResultDto>(json);
        if (result is null || !result.Merged)
            throw new GitOperationException(
                $"GitHub did not merge pull request #{number}: {GitHubApiClient.Redact(result?.Message ?? "not mergeable", token)}");

        // The merge endpoint returns only {merged, sha, message}; project the known facts.
        return new PullRequestItem { Number = number, State = PullRequestState.Merged };
    }

    public async Task CloseAsync(RepoSlug repo, string token, int number, CancellationToken ct)
    {
        var url = $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}/pulls/{number}";
        await _api.SendAsync(new HttpMethod("PATCH"), url, token, "{\"state\":\"closed\"}", ct);
    }

    // ---- JSON → models -------------------------------------------------------------------------

    private static PullRequestItem MapItem(PullDto d) => new()
    {
        Number = d.Number,
        Title = d.Title ?? "",
        Author = d.User?.Login ?? "",
        SourceBranch = d.Head?.Ref ?? "",
        TargetBranch = d.Base?.Ref ?? "",
        IsDraft = d.Draft,
        State = d.MergedAt is not null
            ? PullRequestState.Merged
            : string.Equals(d.State, "closed", StringComparison.OrdinalIgnoreCase)
                ? PullRequestState.Closed
                : d.Draft ? PullRequestState.Draft : PullRequestState.Open,
        Url = d.HtmlUrl ?? "",
    };

    // ---- GitHub JSON shapes (never leave this file) --------------------------------------------

    private sealed class PullDto
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("merged_at")] public string? MergedAt { get; set; }
        [JsonPropertyName("mergeable")] public bool? Mergeable { get; set; }
        [JsonPropertyName("user")] public UserDto? User { get; set; }
        [JsonPropertyName("head")] public RefDto? Head { get; set; }
        [JsonPropertyName("base")] public RefDto? Base { get; set; }
        [JsonPropertyName("requested_reviewers")] public List<UserDto>? RequestedReviewers { get; set; }
    }

    private sealed class UserDto { [JsonPropertyName("login")] public string? Login { get; set; } }
    private sealed class RefDto { [JsonPropertyName("ref")] public string? Ref { get; set; } }

    private sealed class MergeResultDto
    {
        [JsonPropertyName("merged")] public bool Merged { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("sha")] public string? Sha { get; set; }
    }

    private sealed class CreateDto
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("head")] public string Head { get; set; } = "";
        [JsonPropertyName("base")] public string Base { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
    }

    private sealed class MergeDto
    {
        [JsonPropertyName("merge_method")] public string MergeMethod { get; set; } = "merge";
    }
}
