using System;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;

namespace Mainguard.Tests;

// ────────────────────────────────────────────────────────────────────────────────────────────────
// Edition reference-graph gate — ADR-0001 (docs/adr/0001-product-editions.md), Decision 6.
//
// Asserts the edition boundaries that hold on the Phase-2 SPLIT-HEAD layout (step 2f), so a stray
// reference (a `using` + use of a forbidden assembly) becomes a red build instead of a shipped leak.
// THE PAYOFF this step delivers: the edition-agnostic + Client SHELL (Mainguard.App.Shell) references
// ONLY Mainguard.UI + Mainguard.Git — never the agent platform — which is exactly
// what lets the Client exe head's published closure exclude Mainguard.Agents(.UI) / Mainguard.Protos /
// Docker.DotNet / Porta.Pty / Grpc / Dock. The invariants:
//
//   1. The SHELL references NONE of the agent-platform assemblies (THE PAYOFF), and NO shell type uses the
//      container/PTY libraries. Positive control: it DOES reference Mainguard.UI + Mainguard.Git.
//   2. Mainguard.Agents (the platform logic) is UI-free — it must not reach the shell.
//   3. Mainguard.Agents.UI (the split-out Pro UI) must NEVER reference the shell — the ONE-WAY boundary
//      step 2e/2f establishes. Positive control: it reaches the daemon via Grpc.Net.Client + Mainguard.Protos.
//   4. RepoDashboardViewModel (the shared hub) has NO dependency on Mainguard.Agents.Agents (step 1c).
//   5. Mainguard.UI (the base UI layer) references none of the upper/side layers (steps 2c + 2e).
//
// The full CLIENT-HEAD closure gate (parsing Mainguard.Client.App's published .deps.json for the ABSENCE
// of the Agents / Protos / Docker / Grpc assemblies) is verified manually for 2f and automated in 2h.
//
// Mechanism: invariants keyed on ASSEMBLY IDENTITY use GetReferencedAssemblies(). 2g normalized the CLR
// namespaces (Mainguard.UI.* / Mainguard.Agents.UI.* / Mainguard.App.Shell.*), but these checks stay keyed
// on assembly identity — it is the robust signal for "which DLL does this reference", independent of how
// namespaces are sliced. Type-level guards use NetArchTest.Rules (Mono.Cecil), which scans an assembly's own IL.
// ────────────────────────────────────────────────────────────────────────────────────────────────
public class EditionReferenceGraphTests
{
    // Stable anchors into each assembly's own reference graph.
    private static readonly Assembly Shell = typeof(Mainguard.App.Shell.App).Assembly;                       // Mainguard.App.Shell (shell)
    private static readonly Assembly Agents = typeof(Mainguard.Agents.Agents.OrchestratorServices).Assembly; // Mainguard.Agents
    private static readonly Assembly ProUi = typeof(Mainguard.Agents.UI.Editions.ProManifest).Assembly;      // Mainguard.Agents.UI
    private static readonly Assembly BaseUi = typeof(Mainguard.UI.ViewLocator).Assembly;              // Mainguard.UI

    // Invariant 1 (THE PAYOFF, step 2f) — the reference-clean shell references NONE of the agent platform.
    // Keyed on assembly identity (the direct signal for which DLL is referenced).
    [Fact]
    public void Shell_IsReferenceClean_OfTheAgentPlatform()
        => AssertAssemblyDoesNotReference(Shell,
            "Mainguard.Agents", "Mainguard.Agents.UI", "Mainguard.Protos",
            "Grpc.Net.Client", "Grpc.Core.Api", "Docker.DotNet", "Porta.Pty",
            "Dock.Avalonia", "Mainguard.Server");

    // Positive control — the shell genuinely references its two allowed dependencies (else invariant 1
    // could pass vacuously and the shell would not compose at all).
    [Fact]
    public void Shell_ReferencesBaseUiAndGit_PositiveControl()
    {
        AssertAssemblyReferences(Shell, "Mainguard.UI");
        AssertAssemblyReferences(Shell, "Mainguard.Git");
    }

    // Type-level counterpart to invariant 1 — no shell TYPE uses the container/PTY libraries (these ship
    // TRANSITIVELY nowhere near the shell now; this catches a type-level leak even if an assembly ref hid it).
    [Fact]
    public void Shell_NoType_Uses_DockerOrPtyLibraries()
        => AssertNoDependency(Shell, "Docker.DotNet", "Porta.Pty");

    // Invariant 2 — layering: Mainguard.Agents is UI-free and must not reach up into the shell.
    [Fact]
    public void Agents_DoesNotReference_Shell()
        => AssertAssemblyDoesNotReference(Agents, "Mainguard.App.Shell");

    // Invariant 3 (ADR-0001 / steps 2e + 2f) — the ONE-WAY boundary: the Pro-UI assembly
    // (Mainguard.Agents.UI) must NEVER reference the shell (Mainguard.App.Shell). The Pro EXE head
    // references both; this side of the boundary is the cycle 2e/2f exist to prevent.
    [Fact]
    public void ProUi_DoesNotReference_Shell()
        => AssertAssemblyDoesNotReference(ProUi, "Mainguard.App.Shell");

    // Positive control for invariant 3 — the Pro UI genuinely reaches the daemon via the SANCTIONED gRPC
    // seam (the Protos-typed DaemonClient/DaemonBackedOrchestrator moved here in 2e), which is why the shell
    // needs neither Grpc nor Protos.
    [Fact]
    public void ProUi_ReachesDaemonViaGrpc_PositiveControl()
    {
        AssertHasDependency(ProUi, "Grpc.Net.Client");
        AssertHasDependency(ProUi, "Mainguard.Protos");
    }

    // Invariant 5 (ADR-0001 / steps 2c + 2e) — Mainguard.UI is the edition-agnostic BASE UI layer: the
    // shell and the Pro UI sit ON it, so it must reference NONE of the upper/side layers (the shell, the
    // Pro UI, the git/agent logic, or the daemon substrate). Inverting this would collapse the layering.
    [Fact]
    public void BaseUi_DoesNotReference_UpperOrSideLayers()
        => AssertAssemblyDoesNotReference(BaseUi,
            "Mainguard.App.Shell", "Mainguard.Agents.UI", "Mainguard.Agents", "Mainguard.Git",
            "Grpc.Net.Client", "Grpc.Core.Api", "Mainguard.Protos", "Docker.DotNet");

    // Invariant 4 (ADR-0001 / step 1c) — the SHARED git-workspace hub must not reach into the Pro agent
    // platform. The five Pro Tools commands moved out of RepoDashboardViewModel behind IProToolsSurface, so
    // this hub stays in the shell and compiles with NO agent-platform reference at all. Pin it: a future
    // edit that drags the Agents namespace back into the hub turns this red instead of silently re-coupling.
    [Fact]
    public void RepoDashboardViewModel_DoesNotReference_CoreAgents()
    {
        // Guard against a vacuous pass: confirm the hub type is actually selected first.
        var hub = Types.InAssembly(Shell).That().HaveName("RepoDashboardViewModel").GetTypes().ToList();
        Assert.True(
            hub.Count == 1,
            $"Expected exactly one RepoDashboardViewModel in {Shell.GetName().Name}; found {hub.Count}. " +
            "The 1c decoupling guard cannot assert against a type it did not select.");

        var result = Types.InAssembly(Shell)
            .That().HaveName("RepoDashboardViewModel")
            .ShouldNot().HaveDependencyOn("Mainguard.Agents.Agents")
            .GetResult();
        Assert.True(
            result.IsSuccessful,
            "RepoDashboardViewModel (the SHARED git-workspace hub) must not depend on Mainguard.Agents.Agents " +
            "(ADR-0001 / step 1c — the Pro Tools live behind IProToolsSurface/ProToolsSurface). If this " +
            "failed, a Mainguard.Agents.Agents reference leaked back into the hub via these types: " +
            $"{string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    private static void AssertNoDependency(Assembly assembly, params string[] forbidden)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOnAny(forbidden).GetResult();
        Assert.True(
            result.IsSuccessful,
            $"{assembly.GetName().Name} must not reference [{string.Join(", ", forbidden)}] (ADR-0001 " +
            $"edition boundary), but these types do: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }

    private static void AssertHasDependency(Assembly assembly, string required)
    {
        var dependents = Types.InAssembly(assembly).That().HaveDependencyOn(required).GetTypes();
        Assert.True(
            dependents.Any(),
            $"Positive control failed: expected {assembly.GetName().Name} to reference '{required}'. " +
            "A broken analyzer that finds no dependencies would otherwise let every boundary check " +
            "above pass vacuously.");
    }

    // ---- assembly-identity checks (keyed on AssemblyRef metadata — the direct "which DLL" signal,
    //      complementing the type/namespace-keyed NetArchTest guards above) ----

    private static void AssertAssemblyDoesNotReference(Assembly assembly, params string[] forbiddenAssemblyNames)
    {
        var referenced = assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leaked = forbiddenAssemblyNames.Where(referenced.Contains).ToArray();
        Assert.True(
            leaked.Length == 0,
            $"{assembly.GetName().Name} must not reference assembly [{string.Join(", ", leaked)}] (ADR-0001 " +
            "edition boundary, keyed on assembly identity — the direct signal for which DLL is referenced).");
    }

    private static void AssertAssemblyReferences(Assembly assembly, string requiredAssemblyName)
    {
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.True(
            referenced.Contains(requiredAssemblyName, StringComparer.OrdinalIgnoreCase),
            $"Positive control failed: expected {assembly.GetName().Name} to reference assembly " +
            $"'{requiredAssemblyName}' (else the boundary check could pass vacuously).");
    }
}
