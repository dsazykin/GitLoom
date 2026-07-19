using System.Threading;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Services;

namespace Mainguard.Agents.Agents;

/// <summary>
/// Domain error-mapping over the ONE audited git primitive
/// (<see cref="GitService.RunGit"/> — arg-list spawning, <c>GIT_TERMINAL_PROMPT=0</c>,
/// stderr credential redaction). This is NOT a second runner: it spawns nothing itself,
/// it only checks the exit code the shared primitive returns and raises a typed
/// <see cref="RepoProvisioningException"/> on failure. Both P2-06 daemon services route
/// every git call through here so there is exactly one checked path.
/// </summary>
internal static class AgentGitCommand
{
    /// <summary>Runs git in <paramref name="workingDir"/>; throws typed on a non-zero exit. Returns stdout.</summary>
    internal static string Run(string workingDir, params string[] args)
    {
        var (code, output, err) = GitService.RunGit(workingDir, null, CancellationToken.None, args);
        if (code != 0)
        {
            var detail = string.IsNullOrWhiteSpace(err) ? output.Trim() : err.Trim();
            throw new RepoProvisioningException($"git {args[0]} failed (exit {code}): {detail}");
        }

        return output;
    }

    /// <summary>Runs git and returns the raw exit code without throwing (for probe-style checks).</summary>
    internal static int TryRun(string workingDir, out string output, params string[] args)
    {
        var (code, stdout, _) = GitService.RunGit(workingDir, null, CancellationToken.None, args);
        output = stdout;
        return code;
    }
}
