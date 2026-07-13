using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Sandbox;

/// <summary>The verdict for a host against the egress policy.</summary>
public enum EgressVerdict
{
    Allowed,
    Denied,
}

/// <summary>
/// The engine-agnostic default-deny egress seam (P2-07). Fronts the concrete
/// <see cref="EgressProxyConfigurator"/> — an internal Docker network whose only route out is an
/// allowlist-driven proxy, plus pinned DNS and an iptables backstop. No Docker.DotNet types cross
/// this seam so the facade can hold it substrate-agnostically.
/// </summary>
public interface IEgressPolicy
{
    /// <summary>The user-visible, editable, change-logged allowlist (no git-host entry by default — A6).</summary>
    EgressAllowlist Allowlist { get; }

    /// <summary>The internal agent network name (default-deny; the proxy is the only gateway).</summary>
    string NetworkName { get; }

    /// <summary>The proxy URL agents route HTTP(S) egress through (<c>HTTP(S)_PROXY</c>).</summary>
    string ProxyUrl { get; }

    /// <summary>Ensure the internal network + proxy container + pinned DNS + iptables backstop exist (idempotent).</summary>
    Task EnsureReadyAsync(CancellationToken ct = default);

    /// <summary>Policy verdict for a hostname (allowlist match) — the proxy enforces it, this reports it.</summary>
    EgressVerdict Evaluate(string host);
}
