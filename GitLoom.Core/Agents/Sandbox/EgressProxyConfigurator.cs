using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace GitLoom.Core.Agents.Sandbox;

/// <summary>
/// The Docker implementation of <see cref="IEgressPolicy"/> (P2-07 §3.3): a default-deny egress
/// posture built from an internal Docker network whose only route out is an allowlist-driven proxy
/// container. The proxy runs a tinyproxy HTTP(S) CONNECT allowlist + dnsmasq answering only
/// allowlisted names (NXDOMAIN otherwise) + an iptables backstop that DROPs non-proxy egress — so
/// even an agent that ignores <c>HTTP_PROXY</c> and dials a raw IP is dropped (proxy-env-only
/// enforcement is a rejection trigger). The proxy image is built in CI (<c>images/gitloom-egress-proxy/</c>),
/// never at runtime (G-16).
/// </summary>
public sealed class EgressProxyConfigurator : IEgressPolicy
{
    /// <summary>The internal (default-deny) network agents attach to.</summary>
    public const string AgentNetworkName = "gitloom-agents";

    /// <summary>The egress-capable network the proxy's second leg sits on.</summary>
    public const string EgressNetworkName = "gitloom-egress";

    /// <summary>The proxy container name / in-network hostname.</summary>
    public const string ProxyContainerName = "gitloom-egress-proxy";

    /// <summary>The tinyproxy CONNECT listener port.</summary>
    public const int ProxyPort = 8888;

    private readonly IDockerClient _docker;
    private readonly string _proxyImageRef;
    private readonly string? _gatewayUpstream;

    /// <param name="gatewayUpstream">
    /// The P2-08 AI-gateway <c>host:port</c> the proxy routes model-API hosts through (gateway
    /// fronting). Null disables fronting (the model hosts would then reach the provider directly — a
    /// rejection trigger in production; null is for the P2-07-only tests that predate the gateway).
    /// </param>
    public EgressProxyConfigurator(
        IDockerClient docker,
        EgressAllowlist allowlist,
        string proxyImageRef = "gitloom-egress-proxy:latest",
        string? gatewayUpstream = null)
    {
        _docker = docker ?? throw new ArgumentNullException(nameof(docker));
        Allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
        _proxyImageRef = proxyImageRef;
        _gatewayUpstream = gatewayUpstream;
    }

    public EgressAllowlist Allowlist { get; }

    public string NetworkName => AgentNetworkName;

    public string ProxyUrl => $"http://{ProxyContainerName}:{ProxyPort}";

    public EgressVerdict Evaluate(string host) =>
        Allowlist.Allows(host) ? EgressVerdict.Allowed : EgressVerdict.Denied;

    public async Task EnsureReadyAsync(CancellationToken ct = default)
    {
        // Internal network: no route out except via a container with a second (egress) leg.
        var internalId = await EnsureNetworkAsync(AgentNetworkName, isInternal: true, ct).ConfigureAwait(false);
        var egressId = await EnsureNetworkAsync(EgressNetworkName, isInternal: false, ct).ConfigureAwait(false);

        var proxyId = await FindContainerIdAsync(ProxyContainerName, ct).ConfigureAwait(false);
        if (proxyId is null)
        {
            var created = await _docker.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Name = ProxyContainerName,
                Hostname = ProxyContainerName,
                Image = _proxyImageRef,
                Labels = new Dictionary<string, string> { ["gitloom.role"] = "egress-proxy" },
                HostConfig = new HostConfig
                {
                    NetworkMode = AgentNetworkName,
                    // The proxy needs NET_ADMIN to install the iptables backstop; nothing else.
                    CapDrop = new List<string> { "ALL" },
                    CapAdd = new List<string> { "NET_ADMIN", "NET_RAW" },
                    SecurityOpt = new List<string> { "no-new-privileges" },
                },
            }, ct).ConfigureAwait(false);
            proxyId = created.ID;

            // Second leg onto the egress-capable network so the proxy — and only the proxy — can reach upstreams.
            await _docker.Networks.ConnectNetworkAsync(egressId, new NetworkConnectParameters { Container = proxyId }, ct).ConfigureAwait(false);
            await _docker.Containers.StartContainerAsync(proxyId, new ContainerStartParameters(), ct).ConfigureAwait(false);
        }

        await PushConfigAsync(proxyId, ct).ConfigureAwait(false);
    }

    private async Task<string> EnsureNetworkAsync(string name, bool isInternal, CancellationToken ct)
    {
        var existing = await _docker.Networks.ListNetworksAsync(
            new NetworksListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { [name] = true } },
            }, ct).ConfigureAwait(false);
        var match = existing.FirstOrDefault(n => n.Name == name);
        if (match is not null) return match.ID;

        var created = await _docker.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge",
            Internal = isInternal,
            Labels = new Dictionary<string, string> { ["gitloom.role"] = isInternal ? "agent-net" : "egress-net" },
        }, ct).ConfigureAwait(false);
        return created.ID;
    }

    private async Task<string?> FindContainerIdAsync(string name, CancellationToken ct)
    {
        var list = await _docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>> { ["name"] = new Dictionary<string, bool> { ["/" + name] = true } },
        }, ct).ConfigureAwait(false);
        return list.FirstOrDefault(c => c.Names.Any(n => n == "/" + name))?.ID;
    }

    /// <summary>Renders the allowlist to the proxy's config files + backstop script and applies them live.</summary>
    private async Task PushConfigAsync(string proxyId, CancellationToken ct)
    {
        await WriteFileAsync(proxyId, "/etc/gitloom/tinyproxy-filter", EgressProxyConfig.RenderTinyproxyFilter(Allowlist), ct).ConfigureAwait(false);
        // P2-08: front the model-API hosts through the AI gateway (token bucket + budgets + no-raw-429).
        if (_gatewayUpstream is not null)
        {
            await WriteFileAsync(proxyId, "/etc/gitloom/tinyproxy-upstreams",
                EgressProxyConfig.RenderTinyproxyUpstreams(Allowlist, _gatewayUpstream), ct).ConfigureAwait(false);
        }

        await WriteFileAsync(proxyId, "/etc/gitloom/dnsmasq.conf", EgressProxyConfig.RenderDnsmasqConfig(Allowlist), ct).ConfigureAwait(false);
        await WriteFileAsync(proxyId, "/etc/gitloom/backstop.sh", EgressProxyConfig.RenderIptablesScript(ProxyPort), ct).ConfigureAwait(false);
        // The image's entrypoint reloads tinyproxy/dnsmasq and (re)applies the backstop from these paths.
        await ExecAsync(proxyId, new[] { "sh", "/etc/gitloom/reload.sh" }, ct).ConfigureAwait(false);
    }

    private async Task WriteFileAsync(string containerId, string path, string content, CancellationToken ct)
    {
        var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            User = "0",
            AttachStdin = true,
            AttachStdout = true,
            AttachStderr = true,
            Cmd = new List<string> { "sh", "-c", "mkdir -p \"$(dirname \"$1\")\"; cat > \"$1\"", "sh", path },
        }, ct).ConfigureAwait(false);
        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct).ConfigureAwait(false);
        var bytes = Encoding.UTF8.GetBytes(content);
        await stream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        stream.CloseWrite();
        await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecAsync(string containerId, IReadOnlyList<string> cmd, CancellationToken ct)
    {
        var exec = await _docker.Exec.ExecCreateContainerAsync(containerId, new ContainerExecCreateParameters
        {
            User = "0",
            AttachStdout = true,
            AttachStderr = true,
            Cmd = cmd.ToList(),
        }, ct).ConfigureAwait(false);
        using var stream = await _docker.Exec.StartAndAttachContainerExecAsync(exec.ID, tty: false, ct).ConfigureAwait(false);
        await stream.ReadOutputToEndAsync(ct).ConfigureAwait(false);
    }
}
