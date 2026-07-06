using GitLoom.Core.Models;

namespace GitLoom.Core.Sync;

/// <summary>
/// Chooses the right <see cref="IHostProvider"/> for a remote's host + kind (T-14).
/// Resolution keys off <see cref="HostKind"/> (as classified by
/// <c>GitHostDetector.Detect</c>) so a self-hosted GitLab (kind = GitLab, host ≠
/// gitlab.com) still routes to <see cref="GitLabProvider"/>, and anything
/// unrecognized falls back to <see cref="GenericHostProvider"/> (PAT dialog).
/// </summary>
public static class HostProviderRegistry
{
    public static IHostProvider Resolve(string host, HostKind kind, HostAuthContext? context = null) => kind switch
    {
        HostKind.GitHub => new GitHubProvider(host, context),
        HostKind.GitLab => new GitLabProvider(host, context),
        HostKind.Bitbucket => new BitbucketProvider(host, context),
        HostKind.AzureDevOps => new AzureDevOpsProvider(host, context),
        _ => new GenericHostProvider(host, context)
    };
}
