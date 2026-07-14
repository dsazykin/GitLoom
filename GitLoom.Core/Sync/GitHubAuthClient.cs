using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Sync;

/// <summary>
/// GitHub OAuth device-flow facade used by the Clone Dashboard's in-screen GitHub sign-in. As of T-14
/// the device-flow protocol lives in the shared <see cref="DeviceFlowClient"/> (also driven by
/// <see cref="GitHubProvider"/>) — this type is a thin GitHub-configured facade preserving the
/// pre-existing Clone Dashboard sign-in API/behavior. (Repo listing moved to the host-agnostic
/// <see cref="GitLoom.Core.Services.IHostRepositoryService"/> in P2-48.)
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

    public Task<string?> PollForTokenAsync(DeviceFlowResponse deviceFlow, CancellationToken cancellationToken = default)
        => _deviceFlow.PollForTokenAsync(deviceFlow, cancellationToken);
}
