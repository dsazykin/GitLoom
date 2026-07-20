using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Git.Exceptions;
using Microsoft.AspNetCore.Http;

namespace Mainguard.Server.Gateway;

/// <summary>
/// The in-path 429 interception (P2-08 invariant #1). The model host is reachable <b>only</b> via the
/// egress proxy route this fronts; every model request flows through <see cref="ForwardAsync"/>:
/// <list type="number">
///   <item>acquire the shared key's rate budget (FIFO block on the <see cref="AiGateway"/>);</item>
///   <item>forward upstream;</item>
///   <item>on <b>429</b> → <see cref="AiGateway.Report429"/> (pauses the worker's PTY input + marks the
///   agent <c>RateLimited</c>), honor <c>Retry-After</c> with exponential backoff, retry;</item>
///   <item>on success → resume the PTY, clear the rate-limit state, and settle the lease with the
///   actual token usage parsed from the provider response.</item>
/// </list>
/// <b>The agent's CLI never sees the 429 — it sees a delayed 200.</b> The delay hook is injected so the
/// backoff runs on a virtual clock in tests; production passes real <c>Task.Delay</c>.
/// </summary>
public sealed class GatewayForwarder
{
    private readonly AiGateway _gateway;
    private readonly HttpMessageInvoker _upstream;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _defaultEstimate;
    private readonly int _maxAttempts;

    public GatewayForwarder(
        AiGateway gateway,
        HttpMessageInvoker upstream,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int defaultEstimate = 1000,
        int maxAttempts = 8)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        _delay = delay ?? ((d, ct) => d > TimeSpan.Zero ? Task.Delay(d, ct) : Task.CompletedTask);
        _defaultEstimate = defaultEstimate;
        _maxAttempts = Math.Max(1, maxAttempts);
    }

    /// <summary>
    /// Forwards one model request for <paramref name="agentId"/>, absorbing any upstream 429s so the
    /// returned response is always the eventual non-429 upstream response (a delayed 200). Throws
    /// <see cref="BudgetExhaustedException"/> if the agent is over budget (caller pauses, never kills).
    /// </summary>
    public async Task<HttpResponseMessage> ForwardAsync(
        string agentId, HttpRequestMessage request, int? estimatedTokens, CancellationToken ct)
    {
        var estimate = estimatedTokens ?? _defaultEstimate;

        // Buffer the request body once so the request can be replayed across retries.
        var bodyBytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var contentHeaders = request.Content?.Headers.ToList();

        var lease = await _gateway.AcquireAsync(agentId, estimate, ct).ConfigureAwait(false);

        for (var attempt = 1; ; attempt++)
        {
            using var outbound = Clone(request, bodyBytes, contentHeaders);
            var response = await _upstream.SendAsync(outbound, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < _maxAttempts)
            {
                var retryAfter = ParseRetryAfter(response);
                response.Dispose();
                _gateway.Report429(agentId, retryAfter);       // pauses PTY input, marks RateLimited
                await _delay(_gateway.RemainingBackoff(agentId), ct).ConfigureAwait(false);
                continue;                                       // retry — the CLI still waits on one call
            }

            // Terminal response: buffer it so we can read usage AND still hand it to the caller intact.
            await response.Content.LoadIntoBufferAsync().ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (tokens, model) = ModelUsageParser.Parse(body);
            _gateway.Settle(lease, tokens ?? estimate, model);
            _gateway.ClearRateLimit(agentId);                   // resumes PTY input, marks Running
            return response;
        }
    }

    private static HttpRequestMessage Clone(
        HttpRequestMessage source, byte[]? body, List<KeyValuePair<string, IEnumerable<string>>>? contentHeaders)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            if (contentHeaders is not null)
            {
                foreach (var header in contentHeaders)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }
        }

        return clone;
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta)
            {
                return delta;
            }

            if (ra.Date is { } date)
            {
                var diff = date - DateTimeOffset.UtcNow;
                return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }

        // Fake/raw endpoints may send a bare "Retry-After: 5" header the typed parser missed.
        if (response.Headers.TryGetValues("retry-after", out var values) &&
            int.TryParse(values.FirstOrDefault(), out var seconds))
        {
            return TimeSpan.FromSeconds(seconds);
        }

        return null;
    }
}

/// <summary>
/// Pulls actual token usage + the model id out of a provider response body (Anthropic
/// <c>usage.input_tokens+output_tokens</c>, OpenAI <c>usage.total_tokens</c>). Returns null tokens
/// when the body carries no usage — the caller then settles with the estimate.
/// </summary>
public static class ModelUsageParser
{
    public static (int? Tokens, string Model) Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, string.Empty);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, string.Empty);
            }

            var model = doc.RootElement.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? string.Empty
                : string.Empty;

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return (null, model);
            }

            if (usage.TryGetProperty("total_tokens", out var total) && total.TryGetInt32(out var totalTokens))
            {
                return (totalTokens, model);
            }

            var input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
            var output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt32(out var ov) ? ov : 0;
            var sum = input + output;
            return (sum > 0 ? sum : (int?)null, model);
        }
        catch (JsonException)
        {
            return (null, string.Empty);
        }
    }
}

/// <summary>Resolves which agent an inbound model request belongs to (per-agent listener port).</summary>
public interface IAgentPortMap
{
    /// <summary>The agent bound to a listener <paramref name="port"/>, or null if unknown.</summary>
    string? AgentForPort(int port);
}

/// <summary>
/// The ASP.NET wrapper that puts <see cref="GatewayForwarder"/> on the egress proxy's forward path.
/// Attribution is by <b>per-agent listener port</b> (simplest + test-friendly): the local port the
/// request arrived on maps to an agent id, with an <c>x-mainguard-agent</c> header fallback. Requests to
/// non-model hosts pass through untouched; model hosts are fronted by the gateway (an allowlist entry
/// for a model host without this fronting is a rejection trigger). The forwarding core is
/// <see cref="GatewayForwarder"/> so the no-raw-429 invariant is asserted without spinning a listener.
/// </summary>
public sealed class ModelProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly GatewayForwarder _forwarder;
    private readonly IAgentPortMap _portMap;
    private readonly IReadOnlyCollection<string> _modelHosts;

    public ModelProxyMiddleware(
        RequestDelegate next,
        GatewayForwarder forwarder,
        IAgentPortMap portMap,
        IReadOnlyCollection<string> modelHosts)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));
        _portMap = portMap ?? throw new ArgumentNullException(nameof(portMap));
        _modelHosts = modelHosts ?? Array.Empty<string>();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        if (!IsModelHost(host))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var agentId = context.Connection.LocalPort is var port && _portMap.AgentForPort(port) is { } byPort
            ? byPort
            : context.Request.Headers["x-mainguard-agent"].FirstOrDefault();

        if (string.IsNullOrEmpty(agentId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var request = BuildUpstreamRequest(context, host);
        int? estimate = TryReadEstimate(context);

        try
        {
            using var upstream = await _forwarder.ForwardAsync(agentId, request, estimate, context.RequestAborted)
                .ConfigureAwait(false);
            await WriteBackAsync(context, upstream).ConfigureAwait(false);
        }
        catch (BudgetExhaustedException)
        {
            // The agent is paused with a typed reason (never killed); the CLI receives a soft 402.
            context.Response.StatusCode = StatusCodes.Status402PaymentRequired;
        }
    }

    private bool IsModelHost(string host) =>
        _modelHosts.Any(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));

    private static HttpRequestMessage BuildUpstreamRequest(HttpContext context, string host)
    {
        var uri = new UriBuilder("https", host)
        {
            Path = context.Request.Path,
            Query = context.Request.QueryString.ToString(),
        }.Uri;

        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), uri);
        if (context.Request.ContentLength is > 0 || context.Request.Body.CanRead)
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        return request;
    }

    private static int? TryReadEstimate(HttpContext context) =>
        int.TryParse(context.Request.Headers["x-mainguard-token-estimate"].FirstOrDefault(), out var v) ? v : null;

    private static async Task WriteBackAsync(HttpContext context, HttpResponseMessage upstream)
    {
        context.Response.StatusCode = (int)upstream.StatusCode;
        foreach (var header in upstream.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstream.Content.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        context.Response.Headers.Remove("transfer-encoding");
        var bytes = await upstream.Content.ReadAsByteArrayAsync(context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
