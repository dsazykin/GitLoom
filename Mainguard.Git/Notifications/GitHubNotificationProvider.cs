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
using Mainguard.Git.Services; // shared NotificationMapper

namespace Mainguard.Git.Notifications;

/// <summary>
/// GitHub notifications provider (T-27, REST v3). Speaks GitHub's notifications dialect over the shared
/// <see cref="GitHubApiClient"/> transport — the same audited send + typed-error + redaction path the T-23
/// PR / T-24 issue / T-26 checks providers use (no second HTTP/token path). Three endpoints:
/// <list type="bullet">
///   <item><c>GET /notifications?all={true|false}</c> — the authenticated user's threads. <c>all=false</c>
///   (the request when <c>onlyUnread</c> is true) returns only unread; <c>all=true</c> includes read.</item>
///   <item><c>PATCH /notifications/threads/{id}</c> — mark one thread read.</item>
///   <item><c>PUT /notifications</c> — mark every notification read.</item>
/// </list>
/// <c>reason</c> and <c>subject.type</c> are mapped by the pure <see cref="NotificationMapper"/>; the web URL
/// is a best-effort conversion of the API <c>subject.url</c> (an unlinkable subject yields an empty URL).
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the transport's per-request
/// <c>Authorization: Bearer</c> header — never a URL, argv, log, or exception message; host error text is
/// scrubbed of the token by the shared client. Tests wrap a fixture <see cref="HttpMessageHandler"/> so
/// parsing runs offline.</para>
/// </summary>
internal sealed class GitHubNotificationProvider : INotificationProvider
{
    private readonly GitHubApiClient _api;

    public bool IsImplemented => true;

    /// <param name="http">Shared client; the handler is injected by tests for offline fixtures.</param>
    /// <param name="apiBase">API root (github.com → https://api.github.com; overridable for GHE/tests).</param>
    public GitHubNotificationProvider(HttpClient http, string apiBase = "https://api.github.com")
        => _api = new GitHubApiClient(http, apiBase);

    // TODO(T-27 human-review): live notifications matrix — the endpoints below are fully built and
    // fixture-tested, but the real fetch + mark-read round trip against a GitHub account is host-account-
    // gated and deferred to the manual matrix (mirrors T-23/T-24/T-26's live deferral).

    public async Task<IReadOnlyList<NotificationItem>> ListAsync(string token, bool onlyUnread, CancellationToken ct)
    {
        // all=false → only unread; all=true → include already-read threads.
        var all = onlyUnread ? "false" : "true";
        var json = await _api.SendAsync(HttpMethod.Get, $"{_api.ApiBase}/notifications?all={all}&per_page=100", token, body: null, ct);
        var dtos = GitHubApiClient.Deserialize<List<NotificationDto>>(json) ?? new();
        return dtos.Select(Map).ToList();
    }

    public Task MarkReadAsync(string token, string threadId, CancellationToken ct)
        => _api.SendAsync(HttpMethod.Patch, $"{_api.ApiBase}/notifications/threads/{GitHubApiClient.Esc(threadId)}", token, body: null, ct);

    public Task MarkAllReadAsync(string token, CancellationToken ct)
        => _api.SendAsync(HttpMethod.Put, $"{_api.ApiBase}/notifications", token, body: "{\"read\":true}", ct);

    // ---- JSON → models -------------------------------------------------------------------------

    private static NotificationItem Map(NotificationDto d) => new()
    {
        Id = d.Id ?? "",
        Reason = NotificationMapper.MapReason(d.Reason),
        Kind = NotificationMapper.MapSubjectKind(d.Subject?.Type),
        Title = d.Subject?.Title ?? "",
        RepoFullName = d.Repository?.FullName ?? "",
        Url = ApiToHtmlUrl(d.Subject?.Url),
        Unread = d.Unread,
        UpdatedAt = ParseDate(d.UpdatedAt),
    };

    // Best-effort api→html URL for jump-to (the plan allows best-effort): strip the api host + /repos prefix
    // and singularize the resource segment. A null/blank or unrecognized subject URL yields "" (no jump-to).
    // Release subject URLs (…/releases/{id}) aren't reconstructable to their /releases/tag/… html form and
    // are left as-is — acceptable per the T-27 best-effort contract.
    private static string ApiToHtmlUrl(string? apiUrl)
    {
        if (string.IsNullOrWhiteSpace(apiUrl)) return "";
        var url = apiUrl
            .Replace("https://api.github.com/repos/", "https://github.com/", StringComparison.OrdinalIgnoreCase)
            .Replace("https://api.github.com/", "https://github.com/", StringComparison.OrdinalIgnoreCase)
            .Replace("/pulls/", "/pull/", StringComparison.OrdinalIgnoreCase)
            .Replace("/commits/", "/commit/", StringComparison.OrdinalIgnoreCase);
        return url;
    }

    private static DateTimeOffset ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : default;

    // ---- GitHub JSON shapes (never leave this file) --------------------------------------------

    private sealed class NotificationDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("unread")] public bool Unread { get; set; }
        [JsonPropertyName("reason")] public string? Reason { get; set; }
        [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
        [JsonPropertyName("subject")] public SubjectDto? Subject { get; set; }
        [JsonPropertyName("repository")] public RepositoryDto? Repository { get; set; }
    }

    private sealed class SubjectDto
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class RepositoryDto
    {
        [JsonPropertyName("full_name")] public string? FullName { get; set; }
    }
}
