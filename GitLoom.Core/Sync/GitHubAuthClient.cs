using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace GitLoom.Core.Sync
{
    public class DeviceFlowResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("device_code")]
        public string DeviceCode { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("user_code")]
        public string UserCode { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("verification_uri")]
        public string VerificationUri { get; set; } = string.Empty;

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

    public class GitHubAuthClient
    {
        private string ClientId 
        {
            get 
            {
                var id = Environment.GetEnvironmentVariable("GITHUB_CLIENT_ID");
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new InvalidOperationException("GITHUB_CLIENT_ID environment variable is not set. Please create a .env file.");
                }
                return id;
            }
        }
        
        private readonly HttpClient _httpClient;

        public GitHubAuthClient()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitLoom", "1.0"));
        }

        public async Task<DeviceFlowResponse?> StartDeviceFlowAsync()
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("scope", "repo read:org")
            });

            var response = await _httpClient.PostAsync("https://github.com/login/device/code", content);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<DeviceFlowResponse>(json);
            }

            return null;
        }

        public async Task<string?> PollForTokenAsync(DeviceFlowResponse deviceFlow)
        {
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("device_code", deviceFlow.DeviceCode),
                new KeyValuePair<string, string>("grant_type", "urn:ietf:params:oauth:grant-type:device_code")
            });

            var expiration = DateTime.UtcNow.AddSeconds(deviceFlow.ExpiresIn);
            int intervalMs = deviceFlow.Interval * 1000;

            while (DateTime.UtcNow < expiration)
            {
                var response = await _httpClient.PostAsync("https://github.com/login/oauth/access_token", content);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var tokenResponse = JsonSerializer.Deserialize<AccessTokenResponse>(json);

                    if (!string.IsNullOrEmpty(tokenResponse?.AccessToken))
                    {
                        return tokenResponse.AccessToken;
                    }

                    if (tokenResponse?.Error == "authorization_pending")
                    {
                        await Task.Delay(intervalMs);
                        continue;
                    }
                    else if (tokenResponse?.Error == "slow_down")
                    {
                        intervalMs += 5000;
                        await Task.Delay(intervalMs);
                        continue;
                    }
                    else
                    {
                        // Some other error (expired, denied, etc)
                        break;
                    }
                }
                
                await Task.Delay(intervalMs);
            }

            return null;
        }

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
}
