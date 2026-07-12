using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Security;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-P2-01 — <see cref="ApiKeyHealthService"/> driven fully offline through the
/// <see cref="HttpMessageHandler"/> seam against recorded HTTP-response fixtures (status + headers + body)
/// under <c>Fixtures/ApiKeyHealth/</c>. Covers the per-provider request shape, rate-limit-header parsing,
/// the conservative agent-ceiling table, 401 scrubbing (the key never survives into the reason), the
/// missing-headers floor, and the unreachable→typed-throw contract.
/// </summary>
public class ApiKeyHealthServiceTests
{
    // The sentinel keys embedded in the 401 fixtures, so the scrub assertion is meaningful.
    private const string AnthropicLeakKey = "sk-ant-LEAKEDsentinelKEY0123456789";
    private const string OpenAiLeakKey = "sk-proj-LEAKEDsentinelKEY0123456789";

    // Replays a recorded HTTP response file: first line = status, then headers until a blank line, then body.
    private sealed class RecordedHandler : HttpMessageHandler
    {
        private readonly string _fixture;
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> RequestBodies = new();

        public RecordedHandler(string fixtureName) => _fixture = fixtureName;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); // a real handler honors cancellation
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));

            var raw = Fixture(_fixture);
            // Split header block from body on the first blank line (fixtures use \n line endings).
            var normalized = raw.Replace("\r\n", "\n");
            var sep = normalized.IndexOf("\n\n", StringComparison.Ordinal);
            var headerBlock = sep < 0 ? normalized : normalized[..sep];
            var body = sep < 0 ? "" : normalized[(sep + 2)..];

            var lines = headerBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var statusLine = lines[0]; // e.g. "HTTP/1.1 200 OK"
            var status = (HttpStatusCode)int.Parse(statusLine.Split(' ')[1]);

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            foreach (var line in lines.Skip(1))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                var name = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();
                if (name.Equals("content-type", StringComparison.OrdinalIgnoreCase)) continue; // set on the content
                response.Headers.TryAddWithoutValidation(name, value);
            }
            return response;
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw _ex;
    }

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return File.ReadAllText(Path.Combine(dir ?? AppContext.BaseDirectory, "GitLoom.Tests", "Fixtures", "ApiKeyHealth", name));
    }

    // #1 (plan §7) — Anthropic valid: headers parsed, agents per table.
    [Fact]
    public async Task CheckAsync_Anthropic_ValidKey_ParsesRateLimitHeaders()
    {
        var svc = new ApiKeyHealthService(new RecordedHandler("anthropic_valid.txt"));
        var health = await svc.CheckAsync("anthropic", "sk-ant-real", CancellationToken.None);

        Assert.True(health.IsValid);
        Assert.Equal(1000, health.RequestsPerMinute);
        Assert.Equal(100000, health.TokensPerMinute);
        Assert.Equal(ApiKeyHealthService.EstimateAgents(1000), health.EstimatedConcurrentAgents);
        Assert.Null(health.FailureReason);
    }

    // Assert the outgoing Anthropic request shape: POST /v1/messages, key in x-api-key, version header.
    [Fact]
    public async Task CheckAsync_Anthropic_SendsPostToMessages_WithXApiKeyHeader()
    {
        var handler = new RecordedHandler("anthropic_valid.txt");
        var svc = new ApiKeyHealthService(handler);
        await svc.CheckAsync("anthropic", "sk-ant-real", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
        Assert.Equal("sk-ant-real", req.Headers.GetValues("x-api-key").Single());
        Assert.True(req.Headers.Contains("anthropic-version"));
        Assert.Null(req.Headers.Authorization); // key must NOT be in an Authorization header for Anthropic
    }

    // #2 (plan §7) / TI #3 — OpenAI valid: models endpoint, Bearer header, header names parsed.
    [Fact]
    public async Task CheckAsync_OpenAi_ValidKey_UsesModelsEndpoint_AndBearerHeader_AndParsesHeaders()
    {
        var handler = new RecordedHandler("openai_valid.txt");
        var svc = new ApiKeyHealthService(handler);
        var health = await svc.CheckAsync("openai", "sk-proj-real", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.openai.com/v1/models", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("sk-proj-real", req.Headers.Authorization!.Parameter);

        Assert.True(health.IsValid);
        Assert.Equal(500, health.RequestsPerMinute);
        Assert.Equal(90000, health.TokensPerMinute);
    }

    // #3 (plan §7) / TI #4 — 401 → invalid, reason present, key scrubbed out.
    [Theory]
    [InlineData("anthropic", "anthropic_401.txt", AnthropicLeakKey)]
    [InlineData("openai", "openai_401.txt", OpenAiLeakKey)]
    public async Task CheckAsync_401_IsInvalid_AndScrubbed(string provider, string fixture, string leakKey)
    {
        var svc = new ApiKeyHealthService(new RecordedHandler(fixture));
        var health = await svc.CheckAsync(provider, leakKey, CancellationToken.None);

        Assert.False(health.IsValid);
        Assert.False(string.IsNullOrWhiteSpace(health.FailureReason));
        Assert.DoesNotContain(leakKey, health.FailureReason);
    }

    // #4 (plan §7) / TI #5 — missing headers on a 2xx → valid, null ceilings, floor of 1 agent.
    [Theory]
    [InlineData("anthropic", "anthropic_missing_headers.txt")]
    [InlineData("openai", "openai_missing_headers.txt")]
    public async Task CheckAsync_MissingHeaders_ConservativeFloor(string provider, string fixture)
    {
        var svc = new ApiKeyHealthService(new RecordedHandler(fixture));
        var health = await svc.CheckAsync(provider, "some-key", CancellationToken.None);

        Assert.True(health.IsValid);
        Assert.Null(health.RequestsPerMinute);
        Assert.Null(health.TokensPerMinute);
        Assert.Equal(1, health.EstimatedConcurrentAgents);
    }

    // #5 (plan §7) / TI #6 — transport failure → typed throw, not a KeyHealth result.
    [Fact]
    public async Task CheckAsync_Unreachable_ThrowsTyped()
    {
        var svc = new ApiKeyHealthService(new ThrowingHandler(new HttpRequestException("no route to host")));
        await Assert.ThrowsAsync<GitOperationException>(
            () => svc.CheckAsync("anthropic", "sk-ant-real", CancellationToken.None));
    }

    // Unknown provider → typed throw (not a silent invalid result).
    [Fact]
    public async Task CheckAsync_UnknownProvider_ThrowsTyped()
    {
        var svc = new ApiKeyHealthService(new RecordedHandler("anthropic_valid.txt"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(
            () => svc.CheckAsync("gemini", "key", CancellationToken.None));
        Assert.Contains("gemini", ex.Message);
    }

    // Honors cancellation: a pre-cancelled token surfaces as OperationCanceledException, not a result.
    [Fact]
    public async Task CheckAsync_Cancelled_Throws()
    {
        var svc = new ApiKeyHealthService(new RecordedHandler("anthropic_valid.txt"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => svc.CheckAsync("anthropic", "sk-ant-real", cts.Token));
    }

    // #6 (plan §7) — the ceiling table ascends in BOTH columns (guards future edits).
    [Fact]
    public void CeilingTable_IsMonotonic()
    {
        var table = ApiKeyHealthService.CeilingTableForTests;
        Assert.NotEmpty(table);
        Assert.Equal(0, table[0].MinRpm);   // floor row starts at 0 rpm
        Assert.Equal(1, table[0].Agents);   // floor is 1 agent
        for (var i = 1; i < table.Count; i++)
        {
            Assert.True(table[i].MinRpm > table[i - 1].MinRpm, "MinRpm must strictly ascend");
            Assert.True(table[i].Agents > table[i - 1].Agents, "Agents must strictly ascend");
        }
    }

    // TI #2 — the rpm→agents mapping matches the documented table across representative rows.
    [Theory]
    [InlineData(null, 1)]   // no headers → floor
    [InlineData(0, 1)]
    [InlineData(49, 1)]
    [InlineData(50, 2)]
    [InlineData(100, 4)]
    [InlineData(399, 4)]
    [InlineData(400, 8)]
    [InlineData(1000, 12)]
    [InlineData(5000, 12)]  // never over-promises past the top row
    public void EstimateAgents_MapsRpmToTable(int? rpm, int expected)
    {
        Assert.Equal(expected, ApiKeyHealthService.EstimateAgents(rpm));
    }
}
