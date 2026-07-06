using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;

namespace GitLoom.Core.Sync;

/// <summary>
/// GitHub host provider (T-14): acquires a token via the OAuth device flow, reusing
/// the shared <see cref="DeviceFlowClient"/> extracted from the original
/// <see cref="GitHubAuthClient"/> (behavior preserved). The device-code display is
/// delegated to the injected <see cref="HostAuthContext.PresentDeviceCode"/> so Core
/// stays UI-free.
/// </summary>
public sealed class GitHubProvider : HostProviderBase
{
    public const string DefaultClientId = "Ov23liUuOvbYkQubpRtj";

    private readonly HostAuthContext _context;
    private readonly DeviceFlowClient _deviceFlow;

    public GitHubProvider(string host = "github.com", HostAuthContext? context = null, DeviceFlowClient? deviceFlow = null)
        : base(string.IsNullOrEmpty(host) ? "github.com" : host, HostKind.GitHub)
    {
        _context = context ?? HostAuthContext.Empty;
        _deviceFlow = deviceFlow ?? new DeviceFlowClient(
            DefaultClientId,
            "https://github.com/login/device/code",
            "https://github.com/login/oauth/access_token",
            "repo read:org");
    }

    public override bool SupportsDeviceFlow => true;

    public override async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        // TODO(T-14 human-review): live auth matrix — real GitHub device-flow round trip.
        var device = await _deviceFlow.StartDeviceFlowAsync(ct)
            ?? throw new AuthenticationRequiredException($"Could not start GitHub device flow for {Host}.", Host);

        if (_context.PresentDeviceCode is not null)
            await _context.PresentDeviceCode(device);

        var token = await _deviceFlow.PollForTokenAsync(device, ct);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"GitHub device-flow authentication for {Host} did not complete.", Host);

        return token;
    }
}
