namespace GitLoom.Core.Exceptions;

/// <summary>
/// The daemon read-only git proxy (A6) refused a request: either a non-fetch git service
/// (there is <b>no</b> <c>receive-pack</c>/push code path — the refusal is structural) or a
/// fetch of a host+org prefix that is not on the per-repo allowlist. Every refusal is also
/// audited (<c>egress_denied</c>) and transparency-logged before this is thrown.
/// </summary>
public sealed class GitProxyRefusedException : GitLoomException
{
    public GitProxyRefusedException(string message) : base(message) { }
}
