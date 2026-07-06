using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;

namespace GitLoom.Core.Sync;

/// <summary>
/// Base for personal-access-token providers (T-14, PAT-dialog v1): hosts that don't
/// (yet) drive OAuth device flow acquire a token from a modal the user pastes into,
/// via <see cref="HostAuthContext.PromptForPat"/>. Distinct concrete subclasses keep
/// host identity explicit for <see cref="HostProviderRegistry.Resolve"/>.
/// </summary>
public abstract class PatHostProviderBase : HostProviderBase
{
    private readonly HostAuthContext _context;

    protected PatHostProviderBase(string host, HostKind kind, HostAuthContext? context)
        : base(host, kind)
    {
        _context = context ?? HostAuthContext.Empty;
    }

    public override bool SupportsDeviceFlow => false;

    public override async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        if (_context.PromptForPat is null)
            throw new AuthenticationRequiredException($"A personal access token is required for {Host}.", Host);

        var token = await _context.PromptForPat(Host, ct);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No token was provided for {Host}.", Host);

        // TODO(T-14 human-review): live auth matrix — validate the PAT against the host API.
        return token;
    }
}

/// <summary>Bitbucket Cloud PAT provider (username convention <c>x-token-auth</c>).</summary>
public sealed class BitbucketProvider : PatHostProviderBase
{
    public BitbucketProvider(string host = "bitbucket.org", HostAuthContext? context = null)
        : base(string.IsNullOrEmpty(host) ? "bitbucket.org" : host, HostKind.Bitbucket, context) { }
}

/// <summary>Azure DevOps PAT provider (username convention <c>token</c>).</summary>
public sealed class AzureDevOpsProvider : PatHostProviderBase
{
    public AzureDevOpsProvider(string host = "dev.azure.com", HostAuthContext? context = null)
        : base(string.IsNullOrEmpty(host) ? "dev.azure.com" : host, HostKind.AzureDevOps, context) { }
}

/// <summary>Fallback PAT provider for unrecognized / self-hosted hosts (username <c>x-access-token</c>).</summary>
public sealed class GenericHostProvider : PatHostProviderBase
{
    public GenericHostProvider(string host, HostAuthContext? context = null)
        : base(host ?? string.Empty, HostKind.Unknown, context) { }
}
