using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Hosting;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug + CheckStateMapper

namespace Mainguard.Git.Checks;

/// <summary>
/// GitHub CI/checks provider (T-26, REST v3). Speaks GitHub's checks dialect over the shared
/// <see cref="GitHubApiClient"/> transport — the same audited send + typed-error + redaction path the
/// T-23 PR and T-24 issue providers use (no second HTTP/token path). It merges two GitHub surfaces into
/// one <see cref="CommitChecks"/>:
/// <list type="bullet">
///   <item><c>GET /repos/{o}/{r}/commits/{sha}/check-runs</c> — GitHub Actions / app check-runs (with a
///   re-requestable numeric id).</item>
///   <item><c>GET /repos/{o}/{r}/commits/{sha}/status</c> — the legacy combined commit-status API (older
///   CI that posts statuses; these have no re-requestable id, so their <see cref="CheckRunItem.Id"/> is 0).</item>
/// </list>
/// A name present on both surfaces is de-duplicated by <b>name</b>, with the check-run kept (it is the
/// richer, re-runnable source). Re-run is <c>POST /repos/{o}/{r}/check-runs/{id}/rerequest</c>.
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the transport's per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message; host error text is
/// scrubbed of the token by the shared client. Tests wrap a fixture <see cref="HttpMessageHandler"/> so
/// parsing runs offline.</para>
/// </summary>
internal sealed class GitHubCheckProvider : ICheckProvider
{
    private readonly GitHubApiClient _api;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubCheckProvider(HttpClient http, string apiBase = "https://api.github.com")
        => _api = new GitHubApiClient(http, apiBase);

    // TODO(T-26 human-review): live checks matrix — the endpoints below are fully built and fixture-tested,
    // but the real fetch + re-run round trip against a GitHub account is host-account-gated and deferred to
    // the manual matrix (mirrors T-23/T-24's live deferral).

    public async Task<CommitChecks> GetChecksAsync(RepoSlug repo, string token, string sha, CancellationToken ct)
    {
        var escSha = GitHubApiClient.Esc(sha);

        // 1) Actions / app check-runs.
        var runsJson = await _api.SendAsync(HttpMethod.Get, $"{Base(repo)}/commits/{escSha}/check-runs?per_page=100", token, body: null, ct);
        var runsDto = GitHubApiClient.Deserialize<CheckRunsDto>(runsJson) ?? new();
        var runs = (runsDto.CheckRuns ?? new()).Select(MapCheckRun).ToList();

        // 2) Legacy combined commit status.
        var statusJson = await _api.SendAsync(HttpMethod.Get, $"{Base(repo)}/commits/{escSha}/status", token, body: null, ct);
        var statusDto = GitHubApiClient.Deserialize<CombinedStatusDto>(statusJson) ?? new();

        // Merge, de-duplicating by name: a legacy status whose context matches a check-run name is dropped
        // (the check-run is the richer, re-runnable source and wins).
        var seenNames = new HashSet<string>(runs.Select(r => r.Name), StringComparer.Ordinal);
        foreach (var s in statusDto.Statuses ?? new())
        {
            var name = s.Context ?? "";
            if (name.Length == 0 || !seenNames.Add(name)) continue;
            runs.Add(MapLegacyStatus(s));
        }

        return CheckStateMapper.Rollup(sha, runs);
    }

    public Task RerequestAsync(RepoSlug repo, string token, long checkRunId, CancellationToken ct)
        => _api.SendAsync(HttpMethod.Post, $"{Base(repo)}/check-runs/{checkRunId}/rerequest", token, body: null, ct);

    // ---- JSON → models -------------------------------------------------------------------------

    private string Base(RepoSlug repo) =>
        $"{_api.ApiBase}/repos/{GitHubApiClient.Esc(repo.Owner)}/{GitHubApiClient.Esc(repo.Name)}";

    private static CheckRunItem MapCheckRun(CheckRunDto d) => new()
    {
        Id = d.Id,
        Name = d.Name ?? "",
        State = CheckStateMapper.FromCheckRun(d.Status, d.Conclusion),
        RawStatus = d.Status ?? "",
        Conclusion = d.Conclusion,
        DetailsUrl = d.DetailsUrl ?? d.HtmlUrl ?? "",
        CompletedAt = ParseDate(d.CompletedAt),
    };

    // A legacy commit status has no re-requestable check-run id → Id stays 0 (UI hides its Re-run action).
    private static CheckRunItem MapLegacyStatus(StatusDto d) => new()
    {
        Id = 0,
        Name = d.Context ?? "",
        State = CheckStateMapper.FromLegacyStatus(d.State),
        RawStatus = d.State ?? "",
        Conclusion = d.State,
        DetailsUrl = d.TargetUrl ?? "",
        CompletedAt = ParseDate(d.UpdatedAt),
    };

    private static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;

    // ---- GitHub JSON shapes (never leave this file) --------------------------------------------

    private sealed class CheckRunsDto
    {
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("check_runs")] public List<CheckRunDto>? CheckRuns { get; set; }
    }

    private sealed class CheckRunDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
        [JsonPropertyName("details_url")] public string? DetailsUrl { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("completed_at")] public string? CompletedAt { get; set; }
    }

    private sealed class CombinedStatusDto
    {
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("statuses")] public List<StatusDto>? Statuses { get; set; }
    }

    private sealed class StatusDto
    {
        [JsonPropertyName("context")] public string? Context { get; set; }
        [JsonPropertyName("state")] public string? State { get; set; }
        [JsonPropertyName("target_url")] public string? TargetUrl { get; set; }
        [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
    }
}
