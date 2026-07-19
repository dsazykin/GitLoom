using System;
using System.Collections.Generic;
using System.Linq;

namespace GitLoom.App.Services;

/// <summary>One allowlist row as the App sees it. <see cref="DefeatsA6"/> is computed daemon-side
/// (a git-host entry re-opens a direct route the A6 control removed) and surfaced with a warning.</summary>
public sealed record EgressAllowlistItem(string Name, string HostPattern, string Kind, bool DefeatsA6);

/// <summary>
/// The App's seam to the daemon-owned egress allowlist (P2-07). The App reaches sandboxes/egress
/// <b>only</b> through the daemon (ESC-I2/G-18) — this interface is implemented over
/// <c>DaemonClient</c> in production, so the App never references the container-control library or the
/// sandbox/egress engine seams. Every add/remove is change-logged daemon-side.
/// </summary>
public interface IEgressAllowlistGateway
{
    IReadOnlyList<EgressAllowlistItem> List();
    void Add(string name, string hostPattern, string kind);
    void Remove(string hostPattern);
}

/// <summary>
/// A standalone in-memory gateway seeded with the same defaults the daemon ships — used by the render
/// harness / design preview and as a safe default before a live daemon connection. The real gateway
/// forwards to the daemon over gRPC (where the authoritative allowlist + audit log live).
/// </summary>
public sealed class InMemoryEgressAllowlistGateway : IEgressAllowlistGateway
{
    private readonly List<EgressAllowlistItem> _items;

    public InMemoryEgressAllowlistGateway(IEnumerable<EgressAllowlistItem>? seed = null)
        => _items = (seed ?? DefaultSeed).ToList();

    public IReadOnlyList<EgressAllowlistItem> List() => _items.ToArray();

    public void Add(string name, string hostPattern, string kind)
    {
        if (_items.Any(i => string.Equals(i.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase)))
            return;
        _items.Add(new EgressAllowlistItem(name, hostPattern, kind, LooksLikeGitHost(hostPattern)));
    }

    public void Remove(string hostPattern)
        => _items.RemoveAll(i => string.Equals(i.HostPattern, hostPattern, StringComparison.OrdinalIgnoreCase));

    // Client-side mirror of the daemon's git-host heuristic — only to render the warning marker.
    private static bool LooksLikeGitHost(string host)
    {
        var h = (host ?? string.Empty).Trim().ToLowerInvariant();
        return h is "github.com" or "gitlab.com" or "bitbucket.org"
            || h.Contains("dev.azure.com")
            || h.StartsWith("git.", StringComparison.Ordinal)
            || h.EndsWith(".github.com", StringComparison.Ordinal)
            || h.EndsWith(".gitlab.com", StringComparison.Ordinal);
    }

    private static readonly IReadOnlyList<EgressAllowlistItem> DefaultSeed = new[]
    {
        new EgressAllowlistItem("Anthropic API", "api.anthropic.com", "ModelApi", false),
        new EgressAllowlistItem("OpenAI API", "api.openai.com", "ModelApi", false),
        new EgressAllowlistItem("npm registry", "registry.npmjs.org", "PackageRegistry", false),
        new EgressAllowlistItem("PyPI", "pypi.org", "PackageRegistry", false),
        new EgressAllowlistItem("PyPI files", "files.pythonhosted.org", "PackageRegistry", false),
        new EgressAllowlistItem("NuGet API", "api.nuget.org", "PackageRegistry", false),
        new EgressAllowlistItem("crates.io", "crates.io", "PackageRegistry", false),
        new EgressAllowlistItem("Go module proxy", "proxy.golang.org", "PackageRegistry", false),
    };
}
