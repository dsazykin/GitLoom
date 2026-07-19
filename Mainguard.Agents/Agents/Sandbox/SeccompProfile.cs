using System;
using System.Collections.Generic;
using System.IO;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// The seccomp profile applied to every agent container (P2-07, G-15 + G2 control 3). It is the
/// <b>canonical moby/containerd default-deny profile</b> (<c>defaultAction: SCMP_ACT_ERRNO</c>, the
/// standard <c>archMap</c>, and the ~300-syscall allowlist) with the three cross-process
/// memory-inspection syscalls — <c>ptrace</c>, <c>process_vm_readv</c>, <c>process_vm_writev</c> —
/// removed from every allow rule and denied by an explicit <c>SCMP_ACT_ERRNO</c> rule. So the agent
/// gets the full default hardening (<c>mount</c>/<c>bpf</c>/<c>pivot_root</c> stay cap-gated and,
/// under <c>CapDrop ALL</c>, unreachable; <c>kexec_load</c> et al. are default-denied) AND cannot
/// scrape the OOB key <c>K</c> from the supervisor process's memory (OPS §6.1 decision C).
///
/// <para><b>Single source of truth.</b> The profile is the checked-in
/// <c>images/gitloom-agent-base/seccomp.json</c>, embedded into this assembly. <see cref="Json"/>
/// returns that exact content, so what the pure test asserts equals what <c>ContainerSpecBuilder</c>
/// passes in <c>seccomp=&lt;json&gt;</c> equals what the container runs. A custom <c>seccomp=</c> in
/// <c>SecurityOpt</c> <b>replaces</b> Docker's default (it is not additive), which is exactly why this
/// profile reproduces that default rather than overlaying it. It is never <c>unconfined</c>.</para>
/// </summary>
public static class SeccompProfile
{
    private const string ResourceName = "Mainguard.Agents.Agents.Sandbox.seccomp.json";

    /// <summary>The syscalls this profile structurally denies (G2 control 3).</summary>
    public static readonly IReadOnlyList<string> DeniedSyscalls = new[]
    {
        "ptrace",
        "process_vm_readv",
        "process_vm_writev",
    };

    /// <summary>The seccomp action the profile applies to the denied syscalls.</summary>
    public const string DenyAction = "SCMP_ACT_ERRNO";

    /// <summary>
    /// The default-deny profile JSON, loaded once from the embedded
    /// <c>images/gitloom-agent-base/seccomp.json</c>. This is the authoritative content passed to
    /// Docker and asserted by the tests.
    /// </summary>
    public static string Json { get; } = LoadEmbeddedProfile();

    /// <summary>The full <c>SecurityOpt</c> value Docker consumes (<c>seccomp=&lt;json&gt;</c>).</summary>
    public static string SecurityOptValue => "seccomp=" + Json;

    private static string LoadEmbeddedProfile()
    {
        var assembly = typeof(SeccompProfile).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded seccomp profile '{ResourceName}' is missing; it must be embedded from images/gitloom-agent-base/seccomp.json.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
