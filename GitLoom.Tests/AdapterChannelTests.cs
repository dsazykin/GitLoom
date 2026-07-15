using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Adapters;

namespace GitLoom.Tests;

/// <summary>TI-P2-22 #5/#6: the pinned adapter channel — pin survival vs a breaking upstream, hash
/// verification, and in-VM install at the pinned version with a version-matched health probe.</summary>
public class AdapterChannelTests
{
    private static readonly byte[] PayloadVx = Encoding.UTF8.GetBytes("claude-code-payload-1.2.3");
    private static string ShaOf(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ManifestJson(string version, string sha) => $$"""
    {
      "adapters": [
        {
          "id": "claude-code",
          "displayName": "Claude Code",
          "version": "{{version}}",
          "sha256": "{{sha}}",
          "installCmd": ["npm", "install", "-g", "tool@{{version}}"],
          "configShims": [{ "path": "/home/agent/.tool/config", "content": "noninteractive=true" }],
          "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "{{version}}" }
        }
      ]
    }
    """;

    private sealed class FakeSource : IAdapterChannelSource
    {
        public string ManifestToServe = "";
        public byte[] PayloadToServe = Array.Empty<byte>();
        public Task<string> FetchManifestAsync(CancellationToken ct) => Task.FromResult(ManifestToServe);
        public Task<byte[]> FetchPayloadAsync(AdapterSpec spec, CancellationToken ct) => Task.FromResult(PayloadToServe);
    }

    private sealed class FakeCache : IAdapterManifestCache
    {
        private string? _json;
        public FakeCache(string? seed = null) => _json = seed;
        public string? Read() => _json;
        public void Write(string manifestJson) => _json = manifestJson;
    }

    private sealed class FakeInstallHost : IAdapterInstallHost
    {
        public string? InstalledVersion;
        public bool FailProbeAlways;
        public readonly List<IReadOnlyList<string>> Commands = new();
        public readonly Dictionary<string, string> Shims = new();
        private readonly IReadOnlyList<string> _probe = new[] { "tool", "--version" };

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.SequenceEqual(_probe))
            {
                return Task.FromResult(InstalledVersion is null || FailProbeAlways
                    ? new AdapterCommandResult(1, "", "not installed")
                    : new AdapterCommandResult(0, $"tool version {InstalledVersion}", ""));
            }
            // Install: install EXACTLY the version the command names — never "latest". Both shapes the
            // manifest allows are honoured: a registry-style "pkg@<version>" token, and a staged
            // payload path "<id>-<version>.payload" (the hash-verified-file install the pin requires).
            var pinned = command.FirstOrDefault(t => t.Contains('@'));
            if (pinned is not null)
            {
                InstalledVersion = pinned[(pinned.LastIndexOf('@') + 1)..];
            }
            else if (command.FirstOrDefault(t => t.Contains("/stage/", StringComparison.Ordinal)) is { } staged)
            {
                var name = staged[(staged.LastIndexOf('/') + 1)..staged.LastIndexOf('.')];
                InstalledVersion = name[(name.LastIndexOf('-') + 1)..];
            }
            else
            {
                InstalledVersion = "0";
            }

            return Task.FromResult(new AdapterCommandResult(0, "", ""));
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            Shims[path] = content;
            return Task.CompletedTask;
        }

        public Task<string> StagePayloadAsync(string fileName, byte[] content, CancellationToken ct)
        {
            StagedPayloads[fileName] = content;
            return Task.FromResult($"/home/gitloom/gitloom/adapters/stage/{fileName}");
        }

        public readonly Dictionary<string, byte[]> StagedPayloads = new();
    }

    [Fact]
    public async Task Ensure_ShouldInstallInsideVm_AtPinnedVersion_WithHealthProbe()
    {
        var manifest = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        var result = await channel.EnsureAsync("claude-code");

        Assert.Equal(AdapterEnsureResult.Installed, result);
        Assert.Equal("1.2.3", host.InstalledVersion);
        Assert.Contains(host.Commands, c => c.SequenceEqual(new[] { "npm", "install", "-g", "tool@1.2.3" }));
        Assert.Equal("noninteractive=true", host.Shims["/home/agent/.tool/config"]);

        // Idempotent: already-healthy second run installs nothing.
        Assert.Equal(AdapterEnsureResult.AlreadyHealthy, await channel.EnsureAsync("claude-code"));
    }

    [Fact]
    public async Task Ensure_ShouldSurviveBreakingUpstreamRelease()
    {
        // Pin names 1.2.3; the channel "upstream" is now serving a breaking 2.0.0.
        var pinned = ManifestJson("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource
        {
            ManifestToServe = ManifestJson("2.0.0", ShaOf(Encoding.UTF8.GetBytes("breaking-2.0.0"))),
            PayloadToServe = PayloadVx,
        };
        var host = new FakeInstallHost();
        // The cache holds the pinned manifest; EnsureAsync never implicitly refreshes to the breaking one.
        var channel = new AdapterChannel(source, host, new FakeCache(pinned));

        await channel.EnsureAsync("claude-code");

        Assert.Equal("1.2.3", host.InstalledVersion); // NOT 2.0.0 — the pin survived the breaking upstream
        Assert.DoesNotContain(host.Commands, c => c.Any(t => t.Contains("2.0.0")));
    }

    // ---- the dynamic-CLI wiring (agentKind → launch argv) ----

    private static string ManifestWithLaunch(string version, string sha) => $$"""
    {
      "adapters": [
        {
          "id": "claude-code",
          "displayName": "Claude Code",
          "version": "{{version}}",
          "sha256": "{{sha}}",
          "payloadUrl": "https://registry.npmjs.org/pkg/-/pkg-{{version}}.tgz",
          "installCmd": ["npm", "install", "-g", "--prefix", "/home/gitloom/gitloom/adapters", "{payload}"],
          "healthProbe": { "command": ["tool", "--version"], "expectedVersionSubstring": "{{version}}" },
          "launch": ["/opt/gitloom/adapters/bin/claude"]
        }
      ]
    }
    """;

    [Fact]
    public async Task Ensure_ShouldInstallFromTheStagedVerifiedPayload_NotAReDownload()
    {
        // The {payload} placeholder must expand to the staged file: installing from a registry re-resolve
        // would install bytes the sha256 pin never covered — the pin would be decorative.
        var manifest = ManifestWithLaunch("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        await channel.EnsureAsync("claude-code");

        Assert.Equal(PayloadVx, Assert.Single(host.StagedPayloads).Value);
        var install = host.Commands.First(c => c.Contains("install"));
        // The staged name keeps the payload URL's extension — npm dispatches on it (a neutral name makes
        // npm treat the tarball as a directory: ENOTDIR on <file>/package.json).
        Assert.Contains("/home/gitloom/gitloom/adapters/stage/claude-code-1.2.3.tgz", install);
        Assert.DoesNotContain(install, t => t.Contains("{payload}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ensure_ShouldWriteTheLaunchMarker_OnlyAfterAGreenProbe()
    {
        var manifest = ManifestWithLaunch("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        await channel.EnsureAsync("claude-code");

        // The marker is what maps agentKind → the CLI argv the daemon execs in the jail.
        var marker = InstalledAdapterMarker.TryDeserialize(
            host.Shims["/home/gitloom/gitloom/adapters/registry/claude-code.json"]);
        Assert.NotNull(marker);
        Assert.Equal("claude-code", marker!.Id);
        Assert.Equal("1.2.3", marker.Version);
        Assert.Equal(new[] { "/opt/gitloom/adapters/bin/claude" }, marker.Launch);
    }

    [Fact]
    public async Task Ensure_ProbeFails_ShouldNotWriteALaunchMarker()
    {
        // A marker's existence means "runnable". A CLI whose probe never went green must not be
        // advertised to the daemon as launchable.
        var manifest = ManifestWithLaunch("1.2.3", ShaOf(PayloadVx));
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost { FailProbeAlways = true };
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));

        Assert.DoesNotContain(host.Shims, s => s.Key.Contains("registry/"));
    }

    [Fact]
    public async Task Ensure_ShouldRefuseOnHashMismatch()
    {
        // Manifest pins a sha that does NOT match the payload the channel serves.
        var manifest = ManifestJson("1.2.3", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var source = new FakeSource { PayloadToServe = PayloadVx };
        var host = new FakeInstallHost();
        var channel = new AdapterChannel(source, host, new FakeCache(manifest));

        var ex = await Assert.ThrowsAsync<AdapterChannelException>(() => channel.EnsureAsync("claude-code"));
        Assert.Equal(AdapterChannelError.HashMismatch, ex.Error);
        Assert.Null(host.InstalledVersion); // nothing installed after a hash refusal
    }
}
