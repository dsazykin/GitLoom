using System.Collections.Generic;

namespace GitLoom.Core.Exceptions;

/// <summary>
/// The spawn preflight found a required jail image absent from the VM's docker store (field failure
/// 2026-07-17, twice: a fresh <c>GitLoomEnv</c> import AND the tier-2 VM upgrade both leave the
/// store empty — it lives outside <c>/home/gitloom</c>, so the user-data migration correctly skips
/// it). Thrown BEFORE any worktree/jail is made, naming exactly which image(s) are missing and the
/// repair, so the failure is one actionable <c>FailedPrecondition</c> regardless of whether the
/// agent-base or the egress-proxy image is the absent one (the latter previously surfaced as an
/// opaque create failure inside the egress setup).
/// </summary>
public class SandboxImageMissingException : GitLoomException
{
    public SandboxImageMissingException(IReadOnlyCollection<string> missingImageTags)
        : base(ComposeMessage(missingImageTags))
    {
    }

    private static string ComposeMessage(IReadOnlyCollection<string> missingImageTags)
    {
        var names = string.Join(", ", missingImageTags);
        return $"GitLoom OS is missing the sandbox image(s) {names}. GitLoom installs these at "
            + "startup — restart GitLoom and wait for the 'Sandbox images installed' notice, or "
            + "build manually: wsl -d GitLoomEnv -- docker build -t <image>:latest "
            + "<GitLoom dir>/payload/images/<image>.";
    }
}
