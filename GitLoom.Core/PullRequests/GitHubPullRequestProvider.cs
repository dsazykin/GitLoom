using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;

namespace GitLoom.Core.PullRequests;

/// <summary>
/// GitHub pull-request provider (T-23, REST v3). Uses the injected <b>shared</b> <see cref="HttpClient"/>
/// (never a per-call <c>new</c> — socket exhaustion is a rejection trigger); tests wrap a fixture
/// <see cref="HttpMessageHandler"/> so parsing runs offline.
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message. Any host error
/// text folded into a thrown exception is first scrubbed of the token via <see cref="Redact"/>.</para>
/// </summary>
internal sealed class GitHubPullRequestProvider : IPullRequestProvider
{
    private readonly HttpClient _http;
    private readonly string _apiBase;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubPullRequestProvider(HttpClient http, string apiBase = "https://api.github.com")
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _apiBase = apiBase.TrimEnd('/');
    }

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
        var url = $"{_apiBase}/repos/{Esc(repo.Owner)}/{Esc(repo.Name)}/pulls?state={state}&per_page=100";
        var json = await SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dtos = Deserialize<List<PullDto>>(json) ?? new();

        IEnumerable<PullDto> selected = dtos;
        if (filter == PullRequestState.Merged)
            selected = dtos.Where(d => d.MergedAt is not null);
        else if (filter == PullRequestState.Draft)
            selected = dtos.Where(d => d.Draft);

        return selected.Select(MapItem).ToList();
    }

    public async Task<PullRequestDetail> GetAsync(RepoSlug repo, string token, int number, CancellationToken ct)
    {
        var url = $"{_apiBase}/repos/{Esc(repo.Owner)}/{Esc(repo.Name)}/pulls/{number}";
        var json = await SendAsync(HttpMethod.Get, url, token, body: null, ct);
        var dto = Deserialize<PullDto>(json) ?? throw new GitOperationException("GitHub returned an empty pull request response.");
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
        var url = $"{_apiBase}/repos/{Esc(repo.Owner)}/{Esc(repo.Name)}/pulls";
        var payload = JsonSerializer.Serialize(new CreateDto
        {
            Title = request.Title,
            Head = request.SourceBranch,
            Base = request.TargetBranch,
            Body = request.Body,
            Draft = request.IsDraft,
        });
        var json = await SendAsync(HttpMethod.Post, url, token, payload, ct);
        var dto = Deserialize<PullDto>(json) ?? throw new GitOperationException("GitHub returned an empty create-pull-request response.");
        return MapItem(dto);
    }

    public async Task<PullRequestItem> MergeAsync(RepoSlug repo, string token, int number, PullRequestMergeMethod method, CancellationToken ct)
    {
        var url = $"{_apiBase}/repos/{Esc(repo.Owner)}/{Esc(repo.Name)}/pulls/{number}/merge";
        var payload = JsonSerializer.Serialize(new MergeDto
        {
            MergeMethod = method switch
            {
                PullRequestMergeMethod.Squash => "squash",
                PullRequestMergeMethod.Rebase => "rebase",
                _ => "merge",
            },
        });
        var json = await SendAsync(HttpMethod.Put, url, token, payload, ct);
        var result = Deserialize<MergeResultDto>(json);
        if (result is null || !result.Merged)
            throw new GitOperationException(
                $"GitHub did not merge pull request #{number}: {Redact(result?.Message ?? "not mergeable", token)}");

        // The merge endpoint returns only {merged, sha, message}; project the known facts.
        return new PullRequestItem { Number = number, State = PullRequestState.Merged };
    }

    public async Task CloseAsync(RepoSlug repo, string token, int number, CancellationToken ct)
    {
        var url = $"{_apiBase}/repos/{Esc(repo.Owner)}/{Esc(repo.Name)}/pulls/{number}";
        await SendAsync(new HttpMethod("PATCH"), url, token, "{\"state\":\"closed\"}", ct);
    }

    // ---- HTTP + error handling -------------------------------------------------------------

    // Sends a request with the token in the Authorization header ONLY, returns the success body,
    // and turns any non-success/host/network failure into a typed, token-scrubbed exception.
    private async Task<string> SendAsync(HttpMethod method, string url, string token, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        // Token lives here and nowhere else (G-4).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GitLoom", "1.0"));
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine user cancellation — let it propagate untyped
        }
        catch (Exception ex)
        {
            // Network down / DNS / TLS: typed, never carrying the token.
            throw new GitOperationException($"Could not reach GitHub: {Redact(ex.Message, token)}");
        }

        var content = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return content;

        throw ToTypedError(response.StatusCode, content, token);
    }

    // Maps a GitHub error response to the right typed exception, scrubbing the token from any host text.
    private static Exception ToTypedError(HttpStatusCode status, string body, string token)
    {
        var message = Redact(ExtractMessage(body), token);

        if (status == HttpStatusCode.Unauthorized)
            return new AuthenticationRequiredException(
                $"GitHub rejected the stored token: {message}", "github.com");

        if (status == HttpStatusCode.Forbidden &&
            message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return new GitOperationException($"GitHub API rate limit reached: {message}");

        if (status == (HttpStatusCode)422 &&
            message.Contains("already exist", StringComparison.OrdinalIgnoreCase))
            return new GitOperationException("A pull request already exists for this branch.");

        return new GitOperationException($"GitHub request failed ({(int)status}): {message}");
    }

    // Pulls GitHub's human-readable text out of an error body: the top-level "message" plus any
    // nested "errors[].message" (where a create-conflict's "already exists" text actually lives).
    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no response body";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return body;

            var parts = new List<string>();
            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                parts.Add(m.GetString() ?? "");

            if (doc.RootElement.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
            {
                foreach (var err in errs.EnumerateArray())
                    if (err.ValueKind == JsonValueKind.Object &&
                        err.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                        parts.Add(em.GetString() ?? "");
            }

            var joined = string.Join(" — ", parts.Where(p => p.Length > 0));
            return joined.Length > 0 ? joined : body;
        }
        catch (JsonException) { /* not JSON — fall through to the raw body */ }
        return body;
    }

    /// <summary>Scrubs the token from any host/error text so it can never leak into an exception (G-4).</summary>
    private static string Redact(string text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return text;
        return text.Replace(token, "***");
    }

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

    private static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // Path-segment escape so an owner/repo can never inject query/path syntax into the URL.
    private static string Esc(string segment) => Uri.EscapeDataString(segment);

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
