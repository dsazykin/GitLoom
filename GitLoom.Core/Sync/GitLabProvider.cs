using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;

namespace GitLoom.Core.Sync;

/// <summary>
/// GitLab host provider (T-14): acquires a token via the OAuth device flow
/// (RFC 8628), reusing the shared <see cref="DeviceFlowClient"/>. Works for both
/// gitlab.com and self-hosted GitLab — the device-code/token endpoints are derived
/// from <see cref="HostProviderBase.Host"/>, so a self-hosted instance authenticates
/// against its own origin.
/// </summary>
public sealed class GitLabProvider : HostProviderBase
{
    // Placeholder OAuth application id — the real client id is registered as part of
    // the live-auth matrix (see TODO below); wiring/behavior are complete offline.
    public const string DefaultClientId = "gitloom-gitlab-device-flow";

    private readonly HostAuthContext _context;
    private readonly DeviceFlowClient _deviceFlow;

    public GitLabProvider(string host = "gitlab.com", HostAuthContext? context = null, DeviceFlowClient? deviceFlow = null)
        : base(string.IsNullOrEmpty(host) ? "gitlab.com" : host, HostKind.GitLab)
    {
        _context = context ?? HostAuthContext.Empty;
        var baseUrl = $"https://{Host}";
        _deviceFlow = deviceFlow ?? new DeviceFlowClient(
            DefaultClientId,
            $"{baseUrl}/oauth/authorize_device",
            $"{baseUrl}/oauth/token",
            "read_repository write_repository api");
    }

    public override bool SupportsDeviceFlow => true;

    public override async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        // TODO(T-14 human-review): live auth matrix — real GitLab device-flow round trip
        // (requires a registered GitLab OAuth application id).
        var device = await _deviceFlow.StartDeviceFlowAsync(ct)
            ?? throw new AuthenticationRequiredException($"Could not start GitLab device flow for {Host}.", Host);

        if (_context.PresentDeviceCode is not null)
            await _context.PresentDeviceCode(device);

        var token = await _deviceFlow.PollForTokenAsync(device, ct);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"GitLab device-flow authentication for {Host} did not complete.", Host);

        return token;
    }
}
