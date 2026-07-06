using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace GitLoom.Core.Sync;

/// <summary>
/// GitHub OAuth device-flow + repo listing used by the Clone Dashboard. As of
/// T-14 the device-flow protocol itself lives in the shared <see cref="DeviceFlowClient"/>
/// (also driven by <see cref="GitHubProvider"/>) — this type is a thin GitHub-configured
/// facade preserving the pre-existing Clone Dashboard API/behavior, plus the
/// authenticated repo listing.
/// </summary>
public class GitHubAuthClient
{
    private readonly DeviceFlowClient _deviceFlow;

    public GitHubAuthClient()
    {
        _deviceFlow = new DeviceFlowClient(
            GitHubProvider.DefaultClientId,
            "https://github.com/login/device/code",
            "https://github.com/login/oauth/access_token",
            "repo read:org");
    }

    public Task<DeviceFlowResponse?> StartDeviceFlowAsync()
        => _deviceFlow.StartDeviceFlowAsync();

    public Task<string?> PollForTokenAsync(DeviceFlowResponse deviceFlow, System.Threading.CancellationToken cancellationToken = default)
        => _deviceFlow.PollForTokenAsync(deviceFlow, cancellationToken);

    public async Task<List<Models.GitHubRepository>> GetUserRepositoriesAsync(string accessToken)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitLoom", "1.0"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync("https://api.github.com/user/repos?sort=updated&per_page=100");
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadAsStringAsync();
            var repos = JsonSerializer.Deserialize<List<Models.GitHubRepository>>(json);
            return repos ?? new List<Models.GitHubRepository>();
        }

        return new List<Models.GitHubRepository>();
    }
}
