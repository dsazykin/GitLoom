using System.Collections.Generic;
using System.Reflection;

namespace GitLoom.App.Editions;

// ────────────────────────────────────────────────────────────────────────────────────────────────
// Edition-composition seam — ADR-0001 (docs/adr/0001-product-editions.md) / the
// Product_Editions_And_Structural_Sequencing plan. ONE trunk builds every product; a static
// IEditionManifest (selected in App.axaml.cs) decides whether the shell composes the Pro agent
// platform or not. Behavior under the default Pro manifest is byte-for-behavior identical to today.
//
// Step 1a scope: the seam types + the two manifests + App.Edition + a null-safe ControlCenter. The
// descriptors below are DEFINED now but NOT consumed yet — the rail XAML stays hard-coded until 1b,
// ViewLocator stays single-assembly until 1e, and there is no Pro-Tools surface until 1c. In Phase 2
// the Pro manifest moves to its own assembly; the contract types here stay in the shared shell, which
// is why they carry no reference to any Pro-only concrete type.
// ────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// The composition contract one edition satisfies: its product identity, whether it has an agent
/// platform, how it constructs (or declines to construct) the control center, and the shell surfaces
/// (rail sections, settings pages, view assemblies) it contributes. Selected once at startup via
/// <c>App.Edition</c>.
/// </summary>
public interface IEditionManifest
{
    /// <summary>User-facing product name (title bar, about box).</summary>
    string ProductName { get; }

    /// <summary>True when this edition composes the agent orchestration platform (Pro/Cloud); false
    /// for the plain Git client.</summary>
    bool HasAgentPlatform { get; }

    /// <summary>Which first-run experience this edition opens on a fresh machine.</summary>
    EditionFirstRun FirstRun { get; }

    /// <summary>Build the edition's control center, or <c>null</c> when it has no agent platform. The
    /// Pro manifest routes through <c>App.CreateOrchestratorServices</c> so the render harnesses'
    /// mock-injection seam is preserved.</summary>
    IAgentPlatformSurface? CreateControlCenter();

    /// <summary>The Pro agent-platform Tools surface the shared git-workspace hub delegates its five
    /// Pro Tools commands to (step 1c), or <c>null</c> when this edition has no agent platform — in which
    /// case each hub command no-ops and the Tools/File menu items are gated off. The Pro manifest returns
    /// a <c>ProToolsSurface</c>; the Client manifest returns <c>null</c>.</summary>
    IProToolsSurface? ProTools { get; }

    /// <summary>The ordered rail destinations this edition offers (consumed by the data-driven rail in
    /// step 1b; defined now, hard-coded rail unchanged until then).</summary>
    IReadOnlyList<RailSectionDescriptor> Sections { get; }

    /// <summary>True when the shell shows the agent rail (worker list + kill switch) — Pro only.</summary>
    bool ShowsAgentRail { get; }

    /// <summary>The settings pages this edition contributes (not consumed yet — reserved for later).</summary>
    IReadOnlyList<SettingsPageDescriptor> SettingsPages { get; }

    /// <summary>The assemblies whose Views the shell's ViewLocator may resolve (single-assembly today;
    /// multi-assembly in 1e once the Pro surfaces move out).</summary>
    IReadOnlyList<Assembly> ViewAssemblies { get; }
}

/// <summary>The first-run experience an edition opens with on an unprovisioned machine.</summary>
public enum EditionFirstRun
{
    /// <summary>The plain Git client's dedicated clone/open flow (no VM provisioning).</summary>
    ClientClone,

    /// <summary>The Pro/Cloud OOBE that provisions GitLoom OS (the agent runtime).</summary>
    GitLoomOsProvisioning,
}

/// <summary>The optional adornment a rail section carries (attention badge / spend readout / none).</summary>
public enum RailAdornmentKind
{
    None,
    Attention,
    Spend,
}

/// <summary>One rail destination: its stable id, label, icon resource key, whether it needs an open
/// workspace, its adornment, and (later) the ViewModel type its content resolves to.</summary>
public record RailSectionDescriptor(
    string Id,
    string Label,
    string IconResourceKey,
    bool RequiresWorkspace,
    RailAdornmentKind Adornment,
    System.Type? ContentViewModelType);

/// <summary>A settings page an edition contributes (minimal; not consumed yet).</summary>
public record SettingsPageDescriptor(
    string Id,
    string Label,
    System.Type ViewModelType);
