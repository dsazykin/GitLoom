using System;
using Mainguard.Agents.Terminal.Vterm;
using Xunit;

namespace Mainguard.Server.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that <b>skips</b> (visibly) when the P2-18 native libvterm is not
/// loadable, instead of the old <c>if (!Available) return;</c> early return.
///
/// <para>That early return reported a green <b>"Passed"</b> while asserting nothing, so a local run
/// with no <c>MAINGUARD_LIBVTERM</c> looked identical to a real one — the whole grid suite finished
/// in ~70 ms rather than ~3 s and verified precisely zero behaviour. A skip is honest: the runner
/// reports it as skipped, and nobody mistakes it for coverage.</para>
///
/// <para>The skip is unconditional (it does not consult <c>MAINGUARD_REQUIRE_LIBVTERM</c>) so CI
/// output stays clean rather than turning into a wall of identical failures. Enforcement is the job
/// of the single <see cref="LibvtermPresenceTests"/> guard below, which fails loudly when CI demands
/// the engine and it is missing — mirroring <c>EngineCatalogTests</c> in Mainguard.Tests.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresLibvtermFactAttribute : FactAttribute
{
    public RequiresLibvtermFactAttribute()
    {
        if (!LibvtermAvailability.IsSupported)
        {
            Skip = LibvtermAvailability.SkipReason;
        }
    }
}

/// <summary>Native-libvterm availability + the CI "must actually run" signal.</summary>
internal static class LibvtermAvailability
{
    /// <summary>True when the native library loads in this process (cached, never throws).</summary>
    internal static bool IsSupported => VtermSession.IsSupported;

    /// <summary>True when the environment demands the libvterm legs really execute (CI).</summary>
    internal static bool Required =>
        Environment.GetEnvironmentVariable("MAINGUARD_REQUIRE_LIBVTERM") == "1";

    internal const string SkipReason =
        "native libvterm is not loadable — build it (build/libvterm/build.sh) and point " +
        "MAINGUARD_LIBVTERM at the resulting libvterm.so. CI sets MAINGUARD_REQUIRE_LIBVTERM=1, " +
        "where this skip becomes a hard failure via LibvtermPresenceTests.";
}

/// <summary>
/// The per-project merge gate: when CI sets <c>MAINGUARD_REQUIRE_LIBVTERM=1</c>, the libvterm legs
/// in <b>this</b> project must genuinely run. Mainguard.Tests has the same guard
/// (<c>EngineCatalogTests</c>); this project previously had none, so its grid suites could have
/// silently no-opped in CI with nothing going red.
/// </summary>
public sealed class LibvtermPresenceTests
{
    [Fact]
    public void Libvterm_IsAvailable_WhereRequired()
    {
        if (LibvtermAvailability.Required)
        {
            Assert.True(
                LibvtermAvailability.IsSupported,
                "MAINGUARD_REQUIRE_LIBVTERM=1 but native libvterm did not load — the grid suites " +
                "would have skipped, so this run proves nothing about the P2-18 engine.");
        }
    }
}
