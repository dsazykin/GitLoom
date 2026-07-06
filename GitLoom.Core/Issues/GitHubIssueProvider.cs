using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace GitLoom.Core.Issues;

/// <summary>
/// GitHub issue provider (T-24, REST v3). Speaks GitHub's issue dialect over the shared
/// <see cref="GitHubApiClient"/> transport — the same audited send + typed-error + redaction path the
/// T-23 PR provider uses (no second HTTP/token path). Tests wrap a fixture
/// <see cref="HttpMessageHandler"/> so parsing runs offline.
///
/// <para><b>CRITICAL (T-24):</b> GitHub's <c>/issues</c> endpoint also returns pull requests — every item
/// carrying a <c>pull_request</c> object IS a PR and MUST be filtered out of the issue list (invariant 6).</para>
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the transport's per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message; host error text is
/// scrubbed of the token by the shared client.</para>
/// </summary>
internal sealed class GitHubIssueProvider : IIssueProvider
{
    private readonly GitHubApiClient _api;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubIssueProvider(HttpClient http, string apiBase = "https://api.github.com")
        => _api = new GitHubApiClient(http, apiBase);

    // TODO(T-24 human-review): live issues matrix — the endpoints below are fully built and fixture-tested,
    // but the real list/create/comment/close/reopen round trip against a GitHub account is host-account-gated
    // and deferred to the manual matrix (mirrors T-23's live-PR deferral).

    public async Task<IReadOnlyList<IssueItem>> ListAsync(RepoSlug repo, string token, IssueState filter, CancellationToken ct)
    {
        var state = filter == IssueState.Closed ? "closed" : "open";
        var url = $"{Base(repo)}/issues?state={state}&per_page=100";
        var json = await _api.SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dtos = GitHubApiClient.Deserialize<List<IssueDto>>(json) ?? new();

        // CRITICAL: GitHub returns PRs from /issues too — anything with a pull_request object is a PR,
        // never an issue, and must not appear in the issue list.
        return dtos.Where(d => !d.IsPullRequest).Select(MapItem).ToList();
    }

    public async Task<IssueDetail> GetAsync(RepoSlug repo, string token, int number, CancellationToken ct)
    {
        var issueJson = await _api.SendAsync(HttpMethod.Get, $"{Base(repo)}/issues/{number}", token, body: null, ct);
        var dto = GitHubApiClient.Deserialize<IssueDto>(issueJson)
            ?? throw new GitOperationException("GitHub returned an empty issue response.");

        var commentsJson = await _api.SendAsync(HttpMethod.Get, $"{Base(repo)}/issues/{number}/comments?per_page=100", token, body: null, ct);
        var comments = (GitHubApiClient.Deserialize<List<CommentDto>>(commentsJson) ?? new()).Select(MapComment).ToList();

        return new IssueDetail { Summary = MapItem(dto), Body = dto.Body ?? "", Comments = comments };
    }

    public async Task<IssueItem> CreateAsync(RepoSlug repo, string token, CreateIssue request, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new CreateDto
        {
            Title = request.Title,
            Body = request.Body,
            Labels = request.Labels.ToList(),
            Assignees = request.Assignees.ToList(),
        });
        var json = await _api.SendAsync(HttpMethod.Post, $"{Base(repo)}/issues", token, payload, ct);
        var dto = GitHubApiClient.Deserialize<IssueDto>(json)
            ?? throw new GitOperationException("GitHub returned an empty create-issue response.");
        return MapItem(dto);
    }

    public async Task<IssueComment> CommentAsync(RepoSlug repo, string token, int number, string body, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new CommentBodyDto { Body = body });
        var json = await _api.SendAsync(HttpMethod.Post, $"{Base(repo)}/issues/{number}/comments", token, payload, ct);
        var dto = GitHubApiClient.Deserialize<CommentDto>(json)
            ?? throw new GitOperationException("GitHub returned an empty create-comment response.");
        return MapComment(dto);
    }

    public async Task<IssueItem> SetStateAsync(RepoSlug repo, string token, int number, IssueState state, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new StateDto { State = state == IssueState.Closed ? "closed" : "open" });
        var json = await _api.SendAsync(new HttpMethod("PATCH"), $"{Base(repo)}/issues/{number}", token, payload, ct);
        var dto = GitHubApiClient.Deserialize<IssueDto>(json)
            ?? throw new GitOperationException("GitHub returned an empty set-state response.");
        return MapItem(dto);
    }

    // ---- JSON → models -------------------------------------------------------------------------

    private string Base(RepoSlug repo) =>
        $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}";

    private static IssueItem MapItem(IssueDto d) => new()
    {
        Number = d.Number,
        Title = d.Title ?? "",
        Author = d.User?.Login ?? "",
        State = string.Equals(d.State, "closed", StringComparison.OrdinalIgnoreCase) ? IssueState.Closed : IssueState.Open,
        CommentCount = d.Comments,
        Labels = (d.Labels ?? new())
            .Select(l => new IssueLabel { Name = l.Name ?? "", Color = l.Color ?? "" })
            .Where(l => l.Name.Length > 0)
            .ToList(),
        Assignees = (d.Assignees ?? new()).Select(a => a.Login ?? "").Where(s => s.Length > 0).ToList(),
        Url = d.HtmlUrl ?? "",
        UpdatedAt = ParseDate(d.UpdatedAt),
    };

    private static IssueComment MapComment(CommentDto c) => new()
    {
        Author = c.User?.Login ?? "",
        Body = c.Body ?? "",
        When = ParseDate(c.CreatedAt),
    };

    private static DateTimeOffset ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : default;

    // ---- GitHub JSON shapes (never leave this file) --------------------------------------------

    private sealed class IssueDto
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("comments")] public int Comments { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
        [JsonPropertyName("user")] public UserDto? User { get; set; }
        [JsonPropertyName("labels")] public List<LabelDto>? Labels { get; set; }
        [JsonPropertyName("assignees")] public List<UserDto>? Assignees { get; set; }

        // Present only on pull requests returned by the /issues endpoint; its presence is the PR marker.
        [JsonPropertyName("pull_request")] public JsonElement PullRequest { get; set; }

        public bool IsPullRequest => PullRequest.ValueKind == JsonValueKind.Object;
    }

    private sealed class LabelDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("color")] public string? Color { get; set; }
    }

    private sealed class UserDto { [JsonPropertyName("login")] public string? Login { get; set; } }

    private sealed class CommentDto
    {
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
        [JsonPropertyName("user")] public UserDto? User { get; set; }
    }

    private sealed class CreateDto
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("labels")] public List<string> Labels { get; set; } = new();
        [JsonPropertyName("assignees")] public List<string> Assignees { get; set; } = new();
    }

    private sealed class CommentBodyDto
    {
        [JsonPropertyName("body")] public string Body { get; set; } = "";
    }

    private sealed class StateDto
    {
        [JsonPropertyName("state")] public string State { get; set; } = "open";
    }
}
