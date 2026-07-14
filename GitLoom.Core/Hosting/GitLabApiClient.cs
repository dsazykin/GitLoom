using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Hosting;

/// <summary>
/// One audited GitLab REST v4 transport (P2-48), the GitLab counterpart to <see cref="GitHubApiClient"/>.
/// Centralizing the send path means the token-security-critical code lives in exactly <b>one</b> place
/// per host and every host error is mapped to a typed, token-scrubbed exception identically.
///
/// <para>SECURITY (G-4): the token is written <b>only</b> to the per-request <c>Authorization: Bearer</c>
/// header — never a URL, argv, log, or exception message. Host error text folded into a thrown exception
/// is first scrubbed of the token via <see cref="Redact"/>. The <see cref="HttpClient"/> is
/// shared/injected (never a per-call <c>new</c> — socket exhaustion), so headers are set per-request.</para>
/// </summary>
internal sealed class GitLabApiClient
{
    private readonly HttpClient _http;

    /// <summary>API root (gitlab.com → https://gitlab.com/api/v4; a self-hosted origin uses its own host).</summary>
    public string ApiBase { get; }

    public GitLabApiClient(HttpClient http, string apiBase)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ApiBase = apiBase.TrimEnd('/');
    }

    /// <summary>Builds the <c>https://{host}/api/v4</c> base for a GitLab host (gitlab.com or self-hosted).</summary>
    public static string ApiBaseForHost(string host)
    {
        var h = string.IsNullOrWhiteSpace(host) ? "gitlab.com" : host;
        return $"https://{h}/api/v4";
    }

    /// <summary>
    /// Sends a request with the token in the <c>Authorization</c> header ONLY, returns the success body,
    /// and turns any non-success/host/network failure into a typed, token-scrubbed exception.
    /// </summary>
    public async Task<string> SendAsync(HttpMethod method, string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        // Token lives here and nowhere else (G-4).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("GitLoom", "1.0"));

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
            throw new GitOperationException($"Could not reach GitLab: {Redact(ex.Message, token)}");
        }

        var content = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return content;

        throw ToTypedError(response.StatusCode, content, token);
    }

    // Maps a GitLab error response to the right typed exception, scrubbing the token from any host text.
    private static Exception ToTypedError(HttpStatusCode status, string body, string token)
    {
        var message = Redact(ExtractMessage(body), token);

        if (status == HttpStatusCode.Unauthorized)
            return new AuthenticationRequiredException(
                $"GitLab rejected the stored token: {message}", "gitlab.com");

        return new GitOperationException($"GitLab request failed ({(int)status}): {message}");
    }

    // Pulls GitLab's human-readable text out of an error body: GitLab uses "message" or "error"/"error_description".
    private static string ExtractMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "no response body";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return body;

            var parts = new List<string>();
            foreach (var name in new[] { "message", "error_description", "error" })
                if (doc.RootElement.TryGetProperty(name, out var m) && m.ValueKind == JsonValueKind.String)
                    parts.Add(m.GetString() ?? "");

            var joined = string.Join(" — ", parts.Where(p => p.Length > 0));
            return joined.Length > 0 ? joined : body;
        }
        catch (JsonException) { /* not JSON — fall through to the raw body */ }
        return body;
    }

    /// <summary>Scrubs the token from any host/error text so it can never leak into an exception (G-4).</summary>
    public static string Redact(string text, string token) => Http.RedactionExtensions.Redact(text, token);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}
