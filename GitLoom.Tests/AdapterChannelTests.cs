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
        public readonly List<IReadOnlyList<string>> Commands = new();
        public readonly Dictionary<string, string> Shims = new();
        private readonly IReadOnlyList<string> _probe = new[] { "tool", "--version" };

        public Task<AdapterCommandResult> RunAsync(IReadOnlyList<string> command, CancellationToken ct)
        {
            Commands.Add(command);
            if (command.SequenceEqual(_probe))
            {
                return Task.FromResult(InstalledVersion is null
                    ? new AdapterCommandResult(1, "", "not installed")
                    : new AdapterCommandResult(0, $"tool version {InstalledVersion}", ""));
            }
            // Install: install EXACTLY the pinned version the command names (never "latest").
            var pinned = command.FirstOrDefault(t => t.Contains('@'));
            InstalledVersion = pinned is null ? "0" : pinned[(pinned.LastIndexOf('@') + 1)..];
            return Task.FromResult(new AdapterCommandResult(0, "", ""));
        }

        public Task WriteFileAsync(string path, string content, CancellationToken ct)
        {
            Shims[path] = content;
            return Task.CompletedTask;
        }
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
