using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Agents.Adapters;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-22 §J-5 — the DYNAMIC agent-CLI wiring: the bundled starter channel's pins are schema-valid
/// (a bad edit fails CI, never a user's install), and the daemon's catalog maps agentKind → the
/// launch argv off the in-VM install markers.
/// </summary>
public class AgentCliCatalogTests
{
    // ---- the bundled starter manifest ----

    [Fact]
    public void StarterManifest_ShouldParse_AndPinEveryAdapter()
    {
        // The whole point of the strict schema: @latest/ranges/short hashes can't even parse. Running
        // Parse over the SHIPPED manifest means a bad pin edit fails here, not on a user's machine.
        var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());

        Assert.NotEmpty(manifest.Adapters);
        foreach (var spec in manifest.Adapters)
        {
            Assert.True(AdapterManifest.IsPinnedVersion(spec.Version), $"{spec.Id} is not pinned");
            Assert.Equal(64, spec.Sha256.Length);
            Assert.StartsWith("https://", spec.PayloadUrl);
            // Every starter adapter is an AGENT (launchable), so agentKind resolves to a real argv.
            Assert.NotNull(spec.Launch);
            Assert.NotEmpty(spec.Launch!);
            // The install must consume the staged, hash-verified bytes — never re-resolve a registry.
            Assert.Contains(spec.InstallCmd, t => t.Contains(AdapterChannel.PayloadToken, StringComparison.Ordinal));
            // The probe must assert the pinned version, or "installed" would mean nothing.
            Assert.Equal(spec.Version, spec.HealthProbe!.ExpectedVersionSubstring);
        }
    }

    [Fact]
    public void StarterManifest_ShouldOfferClaudeCode()
    {
        var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());
        var claude = manifest.Adapters.FirstOrDefault(a => a.Id == "claude-code");

        Assert.NotNull(claude);
        Assert.Equal("Claude Code", claude!.DisplayName);
        Assert.Equal(AdapterPaths.SandboxMount + "/bin/claude", claude.Launch![0]);
    }

    [Fact]
    public void StarterManifest_ClaudeCode_AutoPermitsItsServiceHosts()
    {
        var manifest = AdapterManifest.Parse(BundledAdapterChannelSource.StarterManifestJson());
        var claude = manifest.Adapters.Single(a => a.Id == "claude-code");

        // The confirmed cause of "coordinator CLI exits 1": claude-code's startup connectivity check
        // to platform.claude.com was blocked by the default-deny proxy. Declaring it here auto-permits
        // it on install so the CLI works out of the box.
        Assert.NotNull(claude.EgressHosts);
        Assert.Contains("platform.claude.com", claude.EgressHosts!);
        Assert.Contains("statsig.anthropic.com", claude.EgressHosts!);
    }

    [Fact]
    public void Marker_RoundTripsEgressHosts_AndTheCatalogSurfacesThem()
    {
        using var dir = new TempDir();
        var hosts = new[] { "platform.claude.com", "statsig.anthropic.com" };
        File.WriteAllText(
            Path.Combine(dir.Path, "claude-code.json"),
            InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(
                "claude-code", "2.1.210", new[] { "/opt/mainguard/adapters/bin/claude" },
                ApiKeyEnvVar: "ANTHROPIC_API_KEY", EgressHosts: hosts)));

        var marker = new InstalledAdapterCatalog(dir.Path).TryGet("claude-code");
        Assert.NotNull(marker);
        Assert.Equal(hosts, marker!.EgressHosts);
    }

    [Fact]
    public void Marker_WithoutEgressHosts_IsBackwardCompatible()
    {
        // A marker written before this field deserializes with null egressHosts (no auto-permit),
        // never a parse failure — re-installing the CLI backfills it.
        var marker = InstalledAdapterMarker.TryDeserialize(
            """{ "id": "claude-code", "version": "2.1.210", "launch": ["/opt/mainguard/adapters/bin/claude"] }""");
        Assert.NotNull(marker);
        Assert.Null(marker!.EgressHosts);
    }

    // ---- the daemon-side catalog (agentKind → launch argv) ----

    [Fact]
    public void Catalog_ShouldMapAgentKindToItsLaunchArgv()
    {
        using var dir = new TempDir();
        WriteMarker(dir.Path, "claude-code", "2.1.210", "/opt/mainguard/adapters/bin/claude");

        var catalog = new InstalledAdapterCatalog(dir.Path);

        Assert.True(catalog.HasAny());
        Assert.Equal(new[] { "/opt/mainguard/adapters/bin/claude" }, catalog.TryGetLaunch("claude-code"));
    }

    [Fact]
    public void Catalog_UnknownKind_ReturnsNull_NotAThrow()
    {
        using var dir = new TempDir();
        WriteMarker(dir.Path, "claude-code", "2.1.210", "/opt/mainguard/adapters/bin/claude");

        Assert.Null(new InstalledAdapterCatalog(dir.Path).TryGetLaunch("not-installed"));
    }

    [Fact]
    public void Catalog_NoAdaptersDir_IsEmpty_NotAThrow()
    {
        // A dev box / fresh VM with nothing installed: spawns must still work (session-only path).
        var catalog = new InstalledAdapterCatalog(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        Assert.False(catalog.HasAny());
        Assert.Empty(catalog.List());
        Assert.Null(catalog.TryGetLaunch("claude-code"));
    }

    [Fact]
    public void Catalog_ShouldSeeAnAdapterInstalledAfterTheFirstRead()
    {
        // Installs happen WHILE the daemon runs — that is what "dynamic" means. A catalog that cached
        // its first read would make a freshly installed CLI unlaunchable until a daemon restart.
        using var dir = new TempDir();
        var catalog = new InstalledAdapterCatalog(dir.Path);
        Assert.False(catalog.HasAny());

        WriteMarker(dir.Path, "codex", "0.144.4", "/opt/mainguard/adapters/bin/codex");

        Assert.Equal(new[] { "/opt/mainguard/adapters/bin/codex" }, catalog.TryGetLaunch("codex"));
    }

    [Fact]
    public void Catalog_MalformedMarker_IsSkipped_NotFatal()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "broken.json"), "{ this is not json");
        WriteMarker(dir.Path, "codex", "0.144.4", "/opt/mainguard/adapters/bin/codex");

        var catalog = new InstalledAdapterCatalog(dir.Path);

        // The good adapter still resolves; one corrupt file can't take the spawn path down.
        Assert.Equal("codex", Assert.Single(catalog.List()).Id);
    }

    // ---- helpers ----

    private static void WriteMarker(string dir, string id, string version, params string[] launch) =>
        File.WriteAllText(
            Path.Combine(dir, $"{id}.json"),
            InstalledAdapterMarker.Serialize(new InstalledAdapterMarker(id, version, launch)));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"mainguard-adapters-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
