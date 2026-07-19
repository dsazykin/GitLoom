using System.Collections.Generic;
using System.Linq;

namespace GitLoom.Core.Exceptions;

/// <summary>One image the spawn preflight rejected, and why: absent from the store, or present but
/// version-skewed (its <c>gitloom.image.version</c> label ≠ the daemon's expected constant).</summary>
/// <param name="ImageRef">The image tag/ref (e.g. <c>gitloom-agent-base:latest</c>).</param>
/// <param name="Stale">True when the image is present but outdated; false when it is missing.</param>
public sealed record SandboxImagePreflightProblem(string ImageRef, bool Stale);

/// <summary>
/// The spawn preflight found a required jail image absent — OR present but version-skewed (field
/// failure 2026-07-17, twice: a fresh <c>GitLoomEnv</c> import AND the tier-2 VM upgrade both leave
/// the store empty — it lives outside <c>/home/gitloom</c>, so the user-data migration correctly
/// skips it — and a Dockerfile change can leave an old, wrong-bytes image behind under the same tag).
/// Thrown BEFORE any worktree/jail is made, naming exactly which image(s) need attention and the
/// repair, so the failure is one actionable <c>FailedPrecondition</c> regardless of whether the
/// agent-base or the egress-proxy image is the culprit (the latter previously surfaced as an opaque
/// create failure inside the egress setup).
/// </summary>
public class SandboxImageMissingException : GitLoomException
{
    /// <summary>All-missing convenience ctor (the presence-only callers/tests).</summary>
    public SandboxImageMissingException(IReadOnlyCollection<string> missingImageTags)
        : this(missingImageTags.Select(t => new SandboxImagePreflightProblem(t, Stale: false)).ToList())
    {
    }

    /// <summary>The reason-tagged ctor — each problem is either missing or outdated.</summary>
    public SandboxImageMissingException(IReadOnlyCollection<SandboxImagePreflightProblem> problems)
        : base(ComposeMessage(problems))
    {
    }

    private static string ComposeMessage(IReadOnlyCollection<SandboxImagePreflightProblem> problems)
    {
        var missing = problems.Where(p => !p.Stale).Select(p => p.ImageRef).ToArray();
        var outdated = problems.Where(p => p.Stale).Select(p => p.ImageRef).ToArray();

        var parts = new List<string>();
        if (missing.Length > 0)
        {
            parts.Add($"missing: {string.Join(", ", missing)}");
        }

        if (outdated.Length > 0)
        {
            parts.Add($"outdated: {string.Join(", ", outdated)}");
        }

        return $"Mainguard OS sandbox image(s) need provisioning ({string.Join("; ", parts)}). Mainguard "
            + "installs and updates these at startup — restart Mainguard and wait for the 'Sandbox images "
            + "installed' notice, or use Tools → Rebuild sandbox images. Manual fallback: "
            + "wsl -d GitLoomEnv -- docker build -t <image>:latest <GitLoom dir>/payload/images/<image>.";
    }
}
