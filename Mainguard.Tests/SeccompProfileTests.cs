using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Pins the shipped seccomp profile as a genuine <b>default-deny</b> profile (the canonical moby
/// default + the three memory-inspection denials), not an ALLOW-all overlay. Because a custom
/// <c>seccomp=</c> replaces Docker's default, this profile is the whole hardening story — so it must
/// deny by default and must not allowlist known-dangerous syscalls.
/// </summary>
public class SeccompProfileTests
{
    private static JsonElement Root() => JsonDocument.Parse(SeccompProfile.Json).RootElement;

    private static IEnumerable<JsonElement> Groups() => Root().GetProperty("syscalls").EnumerateArray();

    private static HashSet<string> NamesWithAction(string action) =>
        Groups()
            .Where(g => g.GetProperty("action").GetString() == action)
            .SelectMany(g => g.GetProperty("names").EnumerateArray().Select(n => n.GetString()!))
            .ToHashSet();

    [Fact]
    public void DefaultAction_IsDenyByDefault_NotAllow()
    {
        Assert.Equal("SCMP_ACT_ERRNO", Root().GetProperty("defaultAction").GetString());
    }

    [Fact]
    public void HasArchMap_AndSubstantialAllowlist()
    {
        Assert.True(Root().GetProperty("archMap").GetArrayLength() >= 1);
        // The real moby allowlist is hundreds of syscalls — a hand-rolled overlay would be tiny.
        Assert.True(NamesWithAction("SCMP_ACT_ALLOW").Count > 200);
    }

    [Fact]
    public void MemoryInspectionSyscalls_AreDenied_AndInNoAllowRule()
    {
        var allowed = NamesWithAction("SCMP_ACT_ALLOW");
        var denied = NamesWithAction("SCMP_ACT_ERRNO");

        foreach (var syscall in SeccompProfile.DeniedSyscalls)
        {
            Assert.Contains(syscall, denied);       // explicitly denied
            Assert.DoesNotContain(syscall, allowed); // and reachable via no allow rule
        }
    }

    [Fact]
    public void DangerousSyscalls_AreNotUnconditionallyAllowed()
    {
        // kexec_load must be in no allow rule at all; bpf/mount/pivot_root must only ever appear in a
        // capability-gated allow group (excluded under CapDrop ALL), never in an unconditional one.
        var unconditionalAllow = Groups()
            .Where(g => g.GetProperty("action").GetString() == "SCMP_ACT_ALLOW")
            .Where(g => !HasCapGate(g))
            .SelectMany(g => g.GetProperty("names").EnumerateArray().Select(n => n.GetString()!))
            .ToHashSet();

        Assert.DoesNotContain("kexec_load", unconditionalAllow);
        Assert.DoesNotContain("bpf", unconditionalAllow);
        Assert.DoesNotContain("mount", unconditionalAllow);
        Assert.DoesNotContain("pivot_root", unconditionalAllow);
    }

    [Fact]
    public void Profile_IsNeverUnconfined()
    {
        Assert.DoesNotContain("unconfined", SeccompProfile.Json);
        Assert.StartsWith("seccomp=", SeccompProfile.SecurityOptValue);
    }

    private static bool HasCapGate(JsonElement group)
        => group.TryGetProperty("includes", out var inc)
           && inc.TryGetProperty("caps", out var caps)
           && caps.ValueKind == JsonValueKind.Array
           && caps.GetArrayLength() > 0;
}
