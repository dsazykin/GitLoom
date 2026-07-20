using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Git.Sync;

/// <summary>
/// OAuth device-flow response (RFC 8628): the user code + verification URL shown
/// to the user, plus the device code and polling cadence. Shared by every
/// device-flow host provider (GitHub, GitLab).
/// </summary>
public class DeviceFlowResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    // GitLab returns the verification URL under verification_uri_complete as well;
    // GitHub uses verification_uri. Keep both so either host deserializes cleanly.
    [System.Text.Json.Serialization.JsonPropertyName("verification_uri_complete")]
    public string? VerificationUriComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("interval")]
    public int Interval { get; set; }
}

public class AccessTokenResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Reusable OAuth 2.0 device-authorization-grant client (RFC 8628). Parameterized
/// by a host's client id + endpoints so GitHub and GitLab (and any future
/// device-flow host) share one implementation instead of duplicating the polling
/// state machine. The <see cref="HttpMessageHandler"/> seam lets tests exercise
/// the protocol offline without a live host.
///
/// SECURITY: the access token only ever travels in the HTTPS request/response
/// body (never argv/URL/logs), and this type never writes it anywhere — callers
/// hand it to <c>SecureKeyring</c>.
/// </summary>
public sealed class DeviceFlowClient
{
    private readonly HttpClient _httpClient;
    private readonly string _clientId;
    private readonly string _deviceCodeUrl;
    private readonly string _tokenUrl;
    private readonly string _scope;

    public DeviceFlowClient(string clientId, string deviceCodeUrl, string tokenUrl, string scope, HttpMessageHandler? handler = null)
    {
        _clientId = clientId;
        _deviceCodeUrl = deviceCodeUrl;
        _tokenUrl = tokenUrl;
        _scope = scope;

        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Mainguard", "1.0"));
    }

    public async Task<DeviceFlowResponse?> StartDeviceFlowAsync(CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("scope", _scope)
        });

        var response = await _httpClient.PostAsync(_deviceCodeUrl, content, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<DeviceFlowResponse>(json);
        }

        return null;
    }

    public async Task<string?> PollForTokenAsync(DeviceFlowResponse deviceFlow, CancellationToken cancellationToken = default)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", _clientId),
            new KeyValuePair<string, string>("device_code", deviceFlow.DeviceCode),
            new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
        });

        var expiration = DateTime.UtcNow.AddSeconds(deviceFlow.ExpiresIn);
        int intervalMs = deviceFlow.Interval * 1000;

        while (DateTime.UtcNow < expiration && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var response = await _httpClient.PostAsync(_tokenUrl, content, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var tokenResponse = JsonSerializer.Deserialize<AccessTokenResponse>(json);

                    if (!string.IsNullOrEmpty(tokenResponse?.AccessToken))
                    {
                        return tokenResponse.AccessToken;
                    }

                    if (tokenResponse?.Error == "authorization_pending")
                    {
                        await Task.Delay(intervalMs, cancellationToken);
                        continue;
                    }
                    else if (tokenResponse?.Error == "slow_down")
                    {
                        intervalMs += 5000;
                        await Task.Delay(intervalMs, cancellationToken);
                        continue;
                    }
                    else
                    {
                        // Some other error (expired, denied, etc)
                        break;
                    }
                }

                await Task.Delay(intervalMs, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        return null;
    }
}
