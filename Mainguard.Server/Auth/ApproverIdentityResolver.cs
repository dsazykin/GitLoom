using System;
using System.Runtime.InteropServices;
using Grpc.Core;

namespace Mainguard.Server.Auth;

/// <summary>
/// Resolves the approver identity <b>daemon-side</b> for a plan approval (OPS SA-1 / F2 — binding). The
/// identity is derived from the authenticated connection, NEVER from a client-supplied field — a client
/// cannot influence it (there is no such proto field). A client-set identity would let token-holding host
/// malware forge an attributable approval; deriving it here removes the trivial audit forgery.
///
/// <para><b>Honest residual (OPS §1.1):</b> the daemon binds loopback and same-host, so the connecting
/// process runs as the same OS user as the daemon under the host-trust boundary; the derived identity is
/// therefore that OS user (SO_PEERCRED uid on Linux / the daemon's own user). Host malware running as the
/// user can still drive approvals with a valid token — a host-un-forgeable presence factor is deferred
/// (OPS §10.1). What this closes is the trivial forgery of a <i>different</i> identity in the request.</para>
/// </summary>
public interface IApproverIdentityResolver
{
    /// <summary>The daemon-derived approver identity for the connection behind <paramref name="context"/>.</summary>
    string Resolve(ServerCallContext context);
}

/// <summary>
/// The default peer-credential resolver. On Linux it reports the daemon's effective uid (the loopback peer
/// is the same host/user under the trust boundary); elsewhere it reports the OS user name. It ignores the
/// request entirely — identity is a property of the connection, not the message.
/// </summary>
public sealed class PeerCredentialIdentityResolver : IApproverIdentityResolver
{
    public string Resolve(ServerCallContext context)
    {
        // The request/message is deliberately never consulted (SA-1/F2). Only the connection matters.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                return $"uid:{geteuid()}";
            }
            catch (Exception)
            {
                // Fall through to the OS user name if the libc call is unavailable.
            }
        }

        return $"os:{Environment.UserName}";
    }

    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();
}
