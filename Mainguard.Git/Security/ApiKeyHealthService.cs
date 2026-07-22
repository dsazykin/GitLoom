using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Http;

namespace Mainguard.Git.Security;

/// <summary>
/// The result of a BYOK key health check (P2-01). Never carries the key: <see cref="FailureReason"/>
/// is scrubbed of it via the single sanctioned <see cref="RedactionExtensions.Redact"/> helper.
/// </summary>
public sealed class KeyHealth
{
    /// <summary>True when the provider accepted the key (any 2xx). 401/403 → false.</summary>
    public bool IsValid { get; init; }

    /// <summary>Human-readable reason the key was rejected, token-scrubbed. Null when valid.</summary>
    public string? FailureReason { get; init; }

    /// <summary>Requests/min ceiling from the provider's rate-limit headers; null when absent.</summary>
    public int? RequestsPerMinute { get; init; }

    /// <summary>Tokens/min ceiling from the provider's rate-limit headers; null when absent.</summary>
    public int? TokensPerMinute { get; init; }

    /// <summary>Conservative concurrent-agent estimate from <see cref="RequestsPerMinute"/> via the
    /// in-code ceiling table. Floors at 1 (headers missing → the floor row).</summary>
    public int EstimatedConcurrentAgents { get; init; }
}

/// <summary>
/// Validates an LLM API key at entry (P2-01) so the user learns their realistic concurrency ceiling
/// <i>before</i> the first 429. The key travels only in the in-memory request header of a single probe
/// call; it is never written to argv, a log, or an exception (invariant 1). The
/// <see cref="HttpMessageHandler"/> ctor seam makes every check fully offline-testable against recorded
/// fixtures (invariant 2) — production passes null and gets a real <see cref="SocketsHttpHandler"/>.
///
/// <para>Transport failures (DNS/TLS/timeout) <b>throw</b> a typed <see cref="GitOperationException"/> —
/// "unreachable" is not "invalid", and the caller renders a retry affordance rather than storing
/// nothing-because-invalid.</para>
/// </summary>
public sealed class ApiKeyHealthService
{
    private readonly HttpClient _http;

    // requests/min ceiling -> conservative concurrent-agent estimate.
    // An interactive CLI agent averages ~10 req/min sustained (bursts higher); we halve for safety and
    // round down, so even a generous tier maps to a modest, non-optimistic agent count. Rows MUST stay
    // ascending in BOTH columns (guarded by CeilingTable_IsMonotonic) — the estimate does a
    // greatest-MinRpm-not-exceeding lookup, which is only well-defined for a monotonic table.
    private static readonly (int MinRpm, int Agents)[] CeilingTable =
    {
        (0, 1),      // headers missing or a tiny/free tier: the conservative floor — always at least 1.
        (50, 2),     // ~50 rpm: a light paid tier — two agents can share it without constant throttling.
        (100, 4),    // ~100 rpm: comfortably parallel small swarm.
        (400, 8),    // ~400 rpm: mid tier.
        (1000, 12),  // ~1000 rpm+: high tier — capped at 12 so the estimate never over-promises.
    };

    /// <param name="handler">Injected transport for offline tests; null → a real
    /// <see cref="SocketsHttpHandler"/>. The handler is disposed with the service's <see cref="HttpClient"/>.</param>
    public ApiKeyHealthService(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: true);
    }

    /// <summary>
    /// Probes <paramref name="provider"/> with <paramref name="apiKey"/> and reports validity + the
    /// concurrency ceiling. Unknown provider → typed throw. Transport failure → typed throw (not a result).
    /// </summary>
    public async Task<KeyHealth> CheckAsync(string provider, string apiKey, CancellationToken ct)
    {
        using var request = BuildRequest(provider, apiKey);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine user cancellation — propagate untyped.
        }
        catch (Exception ex)
        {
            // Network down / DNS / TLS / timeout: typed, never carrying the key. Unreachable != invalid.
            throw new GitOperationException(
                $"Could not reach the '{provider}' API to validate the key: {RedactionExtensions.Redact(ex.Message, apiKey)}");
        }

        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var reason = RedactionExtensions.Redact(ExtractProviderMessage(body), apiKey);
            return new KeyHealth
            {
                IsValid = false,
                FailureReason = string.IsNullOrWhiteSpace(reason)
                    ? $"The {provider} API rejected the key ({(int)response.StatusCode})."
                    : reason,
            };
        }

        if (!response.IsSuccessStatusCode)
        {
            // Any other non-success (e.g. 400 for a malformed probe body) is still "reachable but the key
            // did not clearly validate" — treat as invalid with a scrubbed reason rather than a floor-valid.
            var reason = RedactionExtensions.Redact(ExtractProviderMessage(body), apiKey);
            return new KeyHealth
            {
                IsValid = false,
                FailureReason = string.IsNullOrWhiteSpace(reason)
                    ? $"The {provider} API returned an unexpected status ({(int)response.StatusCode})."
                    : reason,
            };
        }

        var (rpm, tpm) = ParseRateLimits(provider, response);
        return new KeyHealth
        {
            IsValid = true,
            RequestsPerMinute = rpm,
            TokensPerMinute = tpm,
            EstimatedConcurrentAgents = EstimateAgents(rpm),
        };
    }

    private static HttpRequestMessage BuildRequest(string provider, string apiKey)
    {
        switch (provider)
        {
            case "anthropic":
                {
                    // A minimal, one-token probe. The key lives ONLY in the x-api-key header (invariant 1).
                    var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
                    request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                    const string payload =
                        "{\"model\":\"claude-3-5-haiku-20241022\",\"max_tokens\":1," +
                        "\"messages\":[{\"role\":\"user\",\"content\":\"Hi\"}]}";
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    return request;
                }
            case "openai":
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models");
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    return request;
                }
            case "google":
                {
                    // Gemini: a key-authenticated model list. The key lives ONLY in the header
                    // (never the query string — query strings land in server logs).
                    var request = new HttpRequestMessage(
                        HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models");
                    request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
                    return request;
                }
            default:
                throw new GitOperationException($"unknown LLM provider '{provider}'");
        }
    }

    private static (int? Rpm, int? Tpm) ParseRateLimits(string provider, HttpResponseMessage response)
    {
        return provider switch
        {
            "anthropic" => (
                ReadHeaderInt(response, "anthropic-ratelimit-requests-limit"),
                ReadHeaderInt(response, "anthropic-ratelimit-tokens-limit")),
            "openai" => (
                ReadHeaderInt(response, "x-ratelimit-limit-requests"),
                ReadHeaderInt(response, "x-ratelimit-limit-tokens")),
            _ => (null, null),
        };
    }

    private static int? ReadHeaderInt(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            foreach (var v in values)
                if (int.TryParse(v, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
        return null;
    }

    /// <summary>The ceiling table, exposed for the monotonicity + mapping tests (TI-P2-01 #2/#6).</summary>
    internal static IReadOnlyList<(int MinRpm, int Agents)> CeilingTableForTests => CeilingTable;

    // Greatest-MinRpm-not-exceeding lookup over the (monotonic) ceiling table. Null rpm → the floor row.
    // Internal so the [Theory] over table rows can assert the mapping directly.
    internal static int EstimateAgents(int? rpm)
    {
        var agents = CeilingTable[0].Agents; // floor
        if (rpm is null) return agents;
        foreach (var (minRpm, tableAgents) in CeilingTable)
            if (rpm.Value >= minRpm)
                agents = tableAgents;
        return agents;
    }

    // Pulls the provider's human message out of an error body (Anthropic: error.message; OpenAI:
    // error.message), falling back to the top-level "message" or the raw body. The result is scrubbed
    // by the caller before it reaches a KeyHealth.
    private static string ExtractProviderMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return body;

            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object &&
                    err.TryGetProperty("message", out var em) && em.ValueKind == JsonValueKind.String)
                    return em.GetString() ?? "";
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? "";
            }

            if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                return m.GetString() ?? "";

            return body;
        }
        catch (JsonException)
        {
            return body; // not JSON — surface it verbatim (still scrubbed downstream).
        }
    }
}
