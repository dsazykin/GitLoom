using System;
using Mainguard.Agents.Agents;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// MG-2: the wired coordinator spawn shim must re-apply the hard server-side caps. These pin the two
/// branches of <see cref="CoordinatorSpawnGate.Evaluate"/> deterministically (no /proc/meminfo
/// dependency — the admission sampler is injected).
/// </summary>
public sealed class CoordinatorSpawnGateTests
{
    // ~25% used → admission is happy.
    private static AdmissionController Roomy() =>
        new(sampler: () => new MemorySample(MemTotalKb: 16_000_000, MemAvailableKb: 12_000_000));

    // ~94% used → admission refuses.
    private static AdmissionController UnderPressure() =>
        new(sampler: () => new MemorySample(MemTotalKb: 16_000_000, MemAvailableKb: 1_000_000));

    [Fact]
    public void Admits_WhenUnderCap_AndMemoryRoomy()
    {
        Assert.Null(CoordinatorSpawnGate.Evaluate(activeManagedWorkers: 2, maxActiveWorkers: 6, Roomy()));
    }

    [Fact]
    public void Refuses_AtCap_WithCapReason()
    {
        var reason = CoordinatorSpawnGate.Evaluate(activeManagedWorkers: 6, maxActiveWorkers: 6, Roomy());
        Assert.NotNull(reason);
        Assert.Contains("cap", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_OverCap_EvenIfCountOvershot()
    {
        Assert.NotNull(CoordinatorSpawnGate.Evaluate(activeManagedWorkers: 9, maxActiveWorkers: 6, Roomy()));
    }

    [Fact]
    public void Refuses_UnderMemoryPressure_WithAdmissionReason()
    {
        var reason = CoordinatorSpawnGate.Evaluate(activeManagedWorkers: 0, maxActiveWorkers: 6, UnderPressure());
        Assert.NotNull(reason);
        Assert.Contains("memory", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapIsCheckedBeforeAdmission()
    {
        // At the cap AND under memory pressure → the cap reason wins (deterministic, count-based first).
        var reason = CoordinatorSpawnGate.Evaluate(activeManagedWorkers: 6, maxActiveWorkers: 6, UnderPressure());
        Assert.Contains("cap", reason, StringComparison.OrdinalIgnoreCase);
    }
}
