using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Hosting;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug

namespace Mainguard.Git.Commits;

/// <summary>
/// GitHub commit-context provider (T-32, REST v3). For a commit SHA it calls
/// <c>GET /repos/{owner}/{repo}/commits/{sha}/pulls</c> (the pull requests associated with a commit) over
/// the shared <see cref="GitHubApiClient"/> transport — the same audited send + error-mapping path the
/// T-23 PR and T-24 issue providers use — maps the returned pulls to host-agnostic
/// <see cref="PullRequestItem"/>s, then runs the pure <see cref="IssueReferenceParser"/> over each PR's
/// title + body to collect the linked issues (deduped, bare <c>#n</c> attributed to the commit's repo).
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the transport's per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message. Host error text
/// folded into a thrown exception is scrubbed of the token by the shared client. Tests wrap a fixture
/// <see cref="HttpMessageHandler"/> so parsing runs offline.</para>
/// </summary>
internal sealed class GitHubCommitContextProvider : ICommitContextProvider
{
    private readonly GitHubApiClient _api;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubCommitContextProvider(HttpClient http, string apiBase = "https://api.github.com")
        => _api = new GitHubApiClient(http, apiBase);

    // TODO(T-32 human-review): live blame-to-PR — the endpoint below is fully built and fixture-tested,
    // but the real commit→PR→linked-issue round trip against a GitHub account is host-account-gated and
    // deferred to the manual matrix (mirrors the T-23 live-PR deferral).

    public async Task<CommitContextResult> GetForCommitAsync(RepoSlug repo, string token, string sha, CancellationToken ct)
    {
        var repoFullName = $"{repo.Owner}/{repo.Name}";
        var url = $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}/commits/{GitHubApiClient.Esc(sha)}/pulls";
        var json = await _api.SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dtos = GitHubApiClient.Deserialize<List<PullDto>>(json) ?? new();

        var pulls = dtos.Select(MapItem).ToList();

        // Collect linked issues from every associated PR's title + body; a bare "#n" belongs to the
        // commit's own repo, an explicit "owner/repo#n" keeps its repo. Dedup across all PRs.
        var linked = new List<LinkedIssueRef>();
        var seen = new HashSet<(string Repo, int Number)>();
        foreach (var dto in dtos)
        {
            foreach (var reference in IssueReferenceParser.Parse($"{dto.Title}\n{dto.Body}", repoFullName))
            {
                if (seen.Add((reference.RepoFullName.ToLowerInvariant(), reference.Number)))
                    linked.Add(reference);
            }
        }

        return new CommitContextResult { Sha = sha, PullRequests = pulls, LinkedIssues = linked };
    }

    // The commits/{sha}/pulls endpoint returns the same pull objects as the PR list; this maps the subset
    // of fields the context surface needs. Kept local (host JSON never leaves the provider file).
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
        [JsonPropertyName("user")] public UserDto? User { get; set; }
        [JsonPropertyName("head")] public RefDto? Head { get; set; }
        [JsonPropertyName("base")] public RefDto? Base { get; set; }
    }

    private sealed class UserDto { [JsonPropertyName("login")] public string? Login { get; set; } }
    private sealed class RefDto { [JsonPropertyName("ref")] public string? Ref { get; set; } }
}
