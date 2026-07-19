using System;
using System.Linq;
using System.Reflection;
using NetArchTest.Rules;

namespace GitLoom.Tests;

// ────────────────────────────────────────────────────────────────────────────────────────────────
// Edition reference-graph gate — ADR-0001 (docs/adr/0001-product-editions.md), Decision 6.
//
// Asserts the edition boundaries that hold on the Phase-2 split layout, so a stray reference (a `using`
// + use of a forbidden assembly) becomes a red build instead of a shipped leak. The invariants:
//
//   1. GitLoom.App has NO dependency on GitLoom.Server — the daemon-substrate boundary (G-18/ESC-I2):
//      the UI reaches the daemon ONLY over gRPC (Grpc.Net.Client + GitLoom.Protos), never by
//      referencing the Server assembly.
//   2. Mainguard.Agents has NO dependency on GitLoom.App — layering (Core is UI-free).
//   3. GitLoom.App has NO dependency on Docker.DotNet or Porta.Pty — these container/PTY libs live in
//      Core for the daemon side. They ship TRANSITIVELY in App's output via Core, which is fine; the
//      invariant is that no *type in App* references them — an App-assembly reference-graph fact,
//      which is exactly what NetArchTest inspects (it scans App's own IL, not Core's transitive deps).
//   4. RepoDashboardViewModel (the shared hub) has NO dependency on Mainguard.Agents.Agents (step 1c).
//   5. Mainguard.Agents.UI (the split-out Pro UI) has NO dependency on GitLoom.App — the ONE-WAY
//      boundary step 2e establishes: the shell references the Pro UI, never the reverse.
//   6. Mainguard.UI (the base UI layer) references none of the upper/side layers (steps 2c + 2e).
//
// Plus positive controls (App DOES reference Core; App DOES reach the daemon via Grpc.Net.Client +
// GitLoom.Protos) so a broken analyzer that silently finds nothing can't pass every check vacuously.
//
// Mechanism: NetArchTest.Rules (Mono.Cecil) — confirmed to analyze this repo's net10.0 assemblies
// without throwing, and confirmed to turn red (with the offending type named) when a dependency
// really exists. If a future toolchain ever breaks Cecil, swap to a dependency-free reflection test
// over Assembly.GetReferencedAssemblies() — same reference-graph semantics, keyed on assembly name.
//
// Scope: this is the FIRST gate — the reference-graph check against the single-head layout. The full
// client-closure gate (parsing the Client head's published .deps.json for the ABSENCE of the
// Agents / Protos / Docker / Grpc assemblies) is a Phase-2 deliverable: it cannot exist until
// Mainguard.Agents is split and a separate Client exe head exists (ADR-0001 "Explicitly deferred"). These
// are ordinary xUnit tests, so they ride CI's existing build-and-test job — no separate CI job.
// ────────────────────────────────────────────────────────────────────────────────────────────────
public class EditionReferenceGraphTests
{
    // Stable anchors into each assembly's own reference graph.
    private static readonly Assembly App = typeof(GitLoom.App.App).Assembly;                 // GitLoom.App
    private static readonly Assembly Core = typeof(Mainguard.Git.Services.IGitService).Assembly; // Mainguard.Agents
    private static readonly Assembly ProUi = typeof(GitLoom.App.Editions.ProManifest).Assembly;  // Mainguard.Agents.UI
    private static readonly Assembly BaseUi = typeof(GitLoom.App.ViewLocator).Assembly;          // Mainguard.UI

    // Invariant 1 (G-18) — the UI must not reference the daemon substrate assembly.
    [Fact]
    public void App_DoesNotReference_Server()
        => AssertNoDependency(App, "GitLoom.Server");

    // Invariant 3 — the container/PTY libraries are daemon-side (Core-only); no App type may use them.
    [Fact]
    public void App_DoesNotReference_DockerOrPtyLibraries()
        => AssertNoDependency(App, "Docker.DotNet", "Porta.Pty");

    // Invariant 2 — layering: Core is UI-free and must not reach up into the App.
    [Fact]
    public void Core_DoesNotReference_App()
        => AssertNoDependency(Core, "GitLoom.App");

    // Positive control — App genuinely depends on Core, so the analyzer isn't vacuously passing.
    [Fact]
    public void App_DoesReference_Core_PositiveControl()
        => AssertHasDependency(App, "Mainguard.Agents");

    // Positive control / counterpart to invariant 1 — the UI reaches the daemon via the SANCTIONED gRPC
    // seam, which is *why* it needs no Server reference. Step 2e moved the Protos-typed daemon CLIENTS
    // (DaemonClient/DaemonBackedOrchestrator/ITerminalGateway — the GrpcChannel + generated Protos stubs)
    // DOWN into the Pro-UI assembly, so that is where Grpc.Net.Client + GitLoom.Protos are referenced now.
    // The shell still touches gRPC types directly only to CATCH Grpc.Core.RpcException in a few probes
    // (startup env / versions / health probe). If a refactor severed either path the boundary story would
    // be a fiction, so pin both — App to Grpc.Core, the Pro UI to the channel + generated clients.
    [Fact]
    public void App_TouchesGrpcCore_PositiveControl()
        => AssertHasDependency(App, "Grpc.Core");

    [Fact]
    public void ProUi_ReachesDaemonViaGrpc_PositiveControl()
    {
        AssertHasDependency(ProUi, "Grpc.Net.Client");
        AssertHasDependency(ProUi, "GitLoom.Protos");
    }

    // Invariants 5 & 6 are keyed on ASSEMBLY IDENTITY, not namespace: the moved Pro/base types deliberately
    // KEEP their GitLoom.App.* CLR namespaces until the 2g normalization, so a namespace-based NetArchTest
    // check cannot tell the Pro-UI or base-UI assembly from the shell (their own types would match
    // "GitLoom.App"). GetReferencedAssemblies() reads each assembly's own AssemblyRef metadata — the exact
    // reference-graph fact, keyed on the assembly's identity — which is the dependency-free fallback the
    // file header always cited.

    // Invariant 5 (ADR-0001 / step 2e) — the ONE-WAY boundary this step establishes: the Pro-UI assembly
    // (Mainguard.Agents.UI) must NEVER reference the shell (GitLoom.App). The shell references the Pro UI
    // (for the Pro-default manifest + the composition seams it wires down); the reverse is the cycle 2e
    // exists to prevent.
    [Fact]
    public void ProUi_DoesNotReference_App()
        => AssertAssemblyDoesNotReference(ProUi, "GitLoom.App");

    // Positive control — the shell genuinely references the Pro-UI assembly (else invariant 5 could pass
    // vacuously, and the Pro default would not compose).
    [Fact]
    public void App_ReferencesProUi_PositiveControl()
        => AssertAssemblyReferences(App, "Mainguard.Agents.UI");

    // Invariant 6 (ADR-0001 / steps 2c + 2e) — Mainguard.UI is the edition-agnostic BASE UI layer: the
    // shell and the Pro UI sit ON it, so it must reference NONE of the upper/side layers (the shell, the
    // Pro UI, the git/agent logic, or the daemon substrate). Inverting this would collapse the layering.
    [Fact]
    public void BaseUi_DoesNotReference_UpperOrSideLayers()
        => AssertAssemblyDoesNotReference(BaseUi,
            "GitLoom.App", "Mainguard.Agents.UI", "Mainguard.Agents", "Mainguard.Git",
            "Grpc.Net.Client", "Grpc.Core.Api", "GitLoom.Protos", "Docker.DotNet");

    // Invariant 4 (ADR-0001 / step 1c) — the SHARED git-workspace hub must not reach into the Pro agent
    // platform. The five Pro Tools commands moved out of RepoDashboardViewModel behind IProToolsSurface
    // (ProToolsSurface holds the Mainguard.Agents.Agents + Pro-View references now), because that hub stays in the
    // shared shell in Phase 2 and must compile without the Pro UI assembly. Pin it: a future edit that
    // drags the Agents namespace back into the hub turns this red instead of silently re-coupling.
    [Fact]
    public void RepoDashboardViewModel_DoesNotReference_CoreAgents()
    {
        // Guard against a vacuous pass: confirm the hub type is actually selected first (a renamed or
        // mistyped HaveName filter would otherwise let ShouldNot pass over an empty set).
        var hub = Types.InAssembly(App).That().HaveName("RepoDashboardViewModel").GetTypes().ToList();
        Assert.True(
            hub.Count == 1,
            $"Expected exactly one RepoDashboardViewModel in {App.GetName().Name}; found {hub.Count}. " +
            "The 1c decoupling guard cannot assert against a type it did not select.");

        var result = Types.InAssembly(App)
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

    // ---- assembly-identity checks (for boundaries the shared GitLoom.App.* namespaces hide from the
    //      namespace-keyed NetArchTest above; keyed on the AssemblyRef metadata instead) ----

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
            "edition boundary, keyed on assembly identity because the moved types keep GitLoom.App.* " +
            "namespaces until the 2g normalization).");
    }

    private static void AssertAssemblyReferences(Assembly assembly, string requiredAssemblyName)
    {
        var referenced = assembly.GetReferencedAssemblies().Select(a => a.Name);
        Assert.True(
            referenced.Contains(requiredAssemblyName, StringComparer.OrdinalIgnoreCase),
            $"Positive control failed: expected {assembly.GetName().Name} to reference assembly " +
            $"'{requiredAssemblyName}' (else invariant 5 could pass vacuously).");
    }
}
