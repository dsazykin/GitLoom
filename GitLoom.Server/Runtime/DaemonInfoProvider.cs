using System.Reflection;

namespace GitLoom.Server.Runtime;

/// <summary>
/// The data source behind <c>AgentService.GetDaemonInfo</c> (the tier-1 daemon fast-path skew
/// probe): the daemon's own assembly informational version plus the GitLoomOS payload version
/// stamped into <c>/etc/gitloomos-release</c> at payload-build time. Kept out of the gRPC class
/// (validation + dispatch only — P2-02 rejection trigger) and constructed with an overridable
/// release-file path so the read is unit-testable without a VM. An absent or unreadable release
/// stamp (e.g. a <c>--local-dev</c> daemon on Windows) yields an empty payload version — never a
/// throw: the skew probe must always be answerable.
/// </summary>
public sealed class DaemonInfoProvider
{
    /// <summary>Where the GitLoomOS payload build stamps its version (see build/gitloomos/).</summary>
    public const string DefaultReleaseFilePath = "/etc/gitloomos-release";

    private const string VersionKey = "GITLOOMOS_VERSION=";

    private readonly string _releaseFilePath;

    public DaemonInfoProvider(string releaseFilePath = DefaultReleaseFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseFilePath);
        _releaseFilePath = releaseFilePath;
    }

    /// <summary>The GitLoom.Server assembly informational version — what the App compares its own
    /// version against to decide a refresh (build metadata after '+' is stripped client-side).</summary>
    public string DaemonVersion =>
        typeof(DaemonInfoProvider).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(DaemonInfoProvider).Assembly.GetName().Version?.ToString()
        ?? string.Empty;

    /// <summary>The <c>GITLOOMOS_VERSION</c> value from the release stamp; empty when absent.</summary>
    public string PayloadVersion
    {
        get
        {
            try
            {
                return File.Exists(_releaseFilePath)
                    ? ParsePayloadVersion(File.ReadAllText(_releaseFilePath))
                    : string.Empty;
            }
            catch (IOException)
            {
                return string.Empty; // unreadable stamp == absent — the probe never throws
            }
            catch (UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Pure parse of the release-file body: the value of the first
    /// <c>GITLOOMOS_VERSION=</c> line, trimmed; empty when the key is missing.</summary>
    public static string ParsePayloadVersion(string releaseFileContent)
    {
        if (string.IsNullOrEmpty(releaseFileContent))
        {
            return string.Empty;
        }

        foreach (var raw in releaseFileContent.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(VersionKey, StringComparison.Ordinal))
            {
                return line[VersionKey.Length..].Trim();
            }
        }

        return string.Empty;
    }
}
