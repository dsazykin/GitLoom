using System.Linq;
using System.Text.Json;
using Docker.DotNet.Models;
using GitLoom.Core.Agents.Sandbox;
using GitLoom.Core.Exceptions;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The pure, security-critical heart of P2-07: asserts every hardening flag AND the G2 per-container
/// quartet on the create request the daemon hands to <c>CreateContainerAsync</c>. These run on every
/// CI leg (no Docker needed) — they are the floor the RequiresDocker suite stands on.
/// </summary>
public class ContainerSpecBuilderTests
{
    private const string Ext4Worktree = "/home/gitloom/gitloom/worktrees/abc123/agent-1";
    private const int AgentUid = 1000;
    private const int SupervisorUid = 1001;

    private static ContainerSpecRequest ValidRequest(string worktree = Ext4Worktree) =>
        new(
            RepoHash: "abc123def456abc123",
            AgentId: "agent-1",
            WorktreePath: worktree,
            ImageRef: "gitloom-agent-base:latest",
            Limits: new SandboxLimits(4L * 1024 * 1024 * 1024, 256),
            NetworkName: "gitloom-agents",
            Credentials: CredTmpfsSpec.Create(AgentUid, SupervisorUid),
            ProxyUrl: "http://gitloom-egress-proxy:8888",
            UsernsMode: "host");

    [Fact]
    public void Build_SetsAllHardeningFlags()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        var host = create.HostConfig;

        Assert.Contains("no-new-privileges", host.SecurityOpt);
        Assert.Equal("host", host.UsernsMode);
        Assert.Equal(4L * 1024 * 1024 * 1024, host.Memory);
        Assert.Equal(256, host.PidsLimit);
        Assert.True(host.ReadonlyRootfs);
        Assert.False(host.Privileged);

        Assert.Contains("/dev/shm", host.Tmpfs.Keys);
        Assert.Contains("/run/secrets", host.Tmpfs.Keys);

        var mount = Assert.Single(host.Mounts);
        Assert.Equal("bind", mount.Type);
        Assert.Equal(Ext4Worktree, mount.Source);
        Assert.Equal("/workspace", mount.Target);
    }

    [Fact]
    public void Build_SeccompProfile_DeniesMemoryInspectionSyscalls()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        var seccomp = create.HostConfig.SecurityOpt.Single(o => o.StartsWith("seccomp="));
        Assert.DoesNotContain("unconfined", seccomp);

        using var doc = JsonDocument.Parse(seccomp["seccomp=".Length..]);

        // Deny-by-default (the moby default profile), NOT an ALLOW-all overlay — G-15.
        Assert.Equal("SCMP_ACT_ERRNO", doc.RootElement.GetProperty("defaultAction").GetString());

        var syscalls = doc.RootElement.GetProperty("syscalls");
        var denied = syscalls.EnumerateArray()
            .Where(r => r.GetProperty("action").GetString() == "SCMP_ACT_ERRNO")
            .SelectMany(r => r.GetProperty("names").EnumerateArray().Select(n => n.GetString()))
            .ToHashSet();

        Assert.Contains("ptrace", denied);
        Assert.Contains("process_vm_readv", denied);
        Assert.Contains("process_vm_writev", denied);
    }

    [Fact]
    public void Build_DropsAllCaps_AndNeverAddsSysPtrace()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());
        Assert.Contains("ALL", create.HostConfig.CapDrop);
        Assert.DoesNotContain(create.HostConfig.CapAdd, c => c.Contains("SYS_PTRACE"));
    }

    [Fact]
    public void Build_KTmpfs_OwnedBySupervisorUid_DistinctFromAgent_Mode0400()
    {
        var creds = CredTmpfsSpec.Create(AgentUid, SupervisorUid);
        Assert.NotEqual(creds.AgentUid, creds.SupervisorUid);
        Assert.Equal(0b100_000_000, creds.Mode); // 0400
        Assert.Equal("/run/secrets/oob.key", creds.OobKeyPath);

        // The container process runs as the agent uid (it cannot read the supervisor-owned K file).
        var create = ContainerSpecBuilder.Build(ValidRequest());
        Assert.Equal(AgentUid.ToString(), create.User);
    }

    [Fact]
    public void CredTmpfsSpec_SharedUid_ThrowsTyped()
    {
        Assert.Throws<SandboxSpecException>(() => CredTmpfsSpec.Create(1000, 1000));
    }

    [Theory]
    [InlineData("/mnt/c/Users/dev/repo")]
    [InlineData("/mnt/d/work")]
    [InlineData(@"C:\Users\dev\repo")]
    [InlineData(@"\\wsl.localhost\GitLoomEnv\home\dev\repo")]
    [InlineData(@"\\server\share\repo")]
    public void Build_RejectsWindowsAndUncMounts_Typed(string badSource)
    {
        Assert.Throws<SandboxSpecException>(() => ContainerSpecBuilder.Build(ValidRequest(badSource)));
    }

    [Fact]
    public void Build_Env_CarriesProxyOnly_NoSecrets()
    {
        var create = ContainerSpecBuilder.Build(ValidRequest());

        Assert.Contains("HTTP_PROXY=http://gitloom-egress-proxy:8888", create.Env);
        Assert.Contains("HTTPS_PROXY=http://gitloom-egress-proxy:8888", create.Env);
        Assert.Contains(create.Env, e => e.StartsWith("NO_PROXY="));

        // No env var is secret-shaped (G-13): the credential tmpfs is the only secret carrier.
        foreach (var entry in create.Env)
        {
            var name = entry.Split('=', 2)[0].ToUpperInvariant();
            if (name is "HTTP_PROXY" or "HTTPS_PROXY" or "NO_PROXY") continue;
            Assert.DoesNotContain("TOKEN", name);
            Assert.DoesNotContain("SECRET", name);
            Assert.DoesNotContain("KEY", name);
            Assert.DoesNotContain("PASSWORD", name);
        }
    }

    [Fact]
    public void Build_DoesNotSetPtraceScopeSysctl()
    {
        // G2 control 2 (kernel.yama.ptrace_scope) is VM-wide (P2-05); it must NOT be on the create request.
        var create = ContainerSpecBuilder.Build(ValidRequest());
        Assert.True(create.HostConfig.Sysctls is null
            || !create.HostConfig.Sysctls.Keys.Any(k => k.Contains("ptrace_scope")));
    }

    [Fact]
    public void ContainerName_IsStablePerRepoAndAgent()
    {
        var a = ContainerSpecBuilder.ContainerName("abc123def456abc", "agent-1");
        var b = ContainerSpecBuilder.ContainerName("abc123def456abc", "agent-1");
        Assert.Equal(a, b);
        Assert.StartsWith("gitloom-", a);
    }
}
