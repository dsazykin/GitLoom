using System;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// A <see cref="FactAttribute"/> that <b>skips</b> (visibly) when the P2-18 native libvterm is not
/// loadable, instead of the old <c>if (!Available) return;</c> early return.
///
/// <para>That early return reported a green <b>"Passed"</b> while asserting nothing, so a local run
/// with no <c>MAINGUARD_LIBVTERM</c> looked identical to a real one and verified precisely zero
/// behaviour. A skip is honest: the runner reports it as skipped, and nobody mistakes it for
/// coverage.</para>
///
/// <para>The skip is unconditional (it does not consult <c>MAINGUARD_REQUIRE_LIBVTERM</c>) so CI
/// output stays clean rather than a wall of identical failures. Enforcement is the job of the single
/// <see cref="EngineCatalogTests"/> guard, which fails when CI demands the engine and it is
/// missing. (Mainguard.Server.Tests carries the equivalent pair in its Fixtures folder — the two
/// test projects cannot share code today.)</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresLibvtermFactAttribute : FactAttribute
{
    public RequiresLibvtermFactAttribute()
    {
        if (!EngineCatalog.AvailableEngines.Contains(EngineCatalog.Libvterm))
        {
            Skip =
                "native libvterm is not loadable — build it (build/libvterm/build.sh) and point " +
                "MAINGUARD_LIBVTERM at the resulting libvterm.so. CI sets MAINGUARD_REQUIRE_LIBVTERM=1, " +
                "where this skip becomes a hard failure via EngineCatalogTests.";
        }
    }
}
