using System;
using System.Collections.Generic;
using System.IO;
using Mainguard.Agents.Terminal.Vterm;
using Mainguard.Agents.UI.Controls;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// The engine registry the P2-04 suites iterate: both engines run the SAME conformance, coverage,
/// and transcript suites through <see cref="ITerminalEngineHarness"/> (invariant 4). Per engine it
/// resolves the shrink-only allowlist file, the golden suffix, and the input-side encoder pair the
/// coverage matrix drives.
///
/// <para>The interim engine always runs. The libvterm engine runs wherever the native library
/// loads (Linux; see <c>build/libvterm/</c>) and is skipped where it cannot (Windows local-dev —
/// the libvterm engine is daemon/Linux-only by design). CI exports
/// <c>MAINGUARD_REQUIRE_LIBVTERM=1</c>, which turns a silent skip into a hard failure via
/// <see cref="EngineCatalogTests"/> — the P2-18 merge gate cannot pass without the libvterm legs
/// actually executing.</para>
/// </summary>
public static class EngineCatalog
{
    public const string Interim = "interim";
    public const string Libvterm = "libvterm";

    /// <summary>Engines runnable in this process.</summary>
    public static IReadOnlyList<string> AvailableEngines =>
        VtermNative_IsAvailable ? new[] { Interim, Libvterm } : new[] { Interim };

    /// <summary>True when the environment demands the libvterm legs run (CI).</summary>
    public static bool LibvtermRequired =>
        Environment.GetEnvironmentVariable("MAINGUARD_REQUIRE_LIBVTERM") == "1";

    private static bool VtermNative_IsAvailable
    {
        get
        {
            try
            {
                using var probe = new VtermSession(2, 2);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public static ITerminalEngineHarness Create(string engine) => engine switch
    {
        Interim => new InterimEngineHarness(),
        Libvterm => new LibvtermEngineHarness(),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown terminal engine."),
    };

    /// <summary>The engine's shrink-only known-failures allowlist (one file per engine; the CI
    /// diff guard protects both).</summary>
    public static IReadOnlySet<string> AllowlistFor(string engine) =>
        TerminalHarnessPaths.LoadAllowlist(AllowlistFileFor(engine));

    public static string AllowlistFileFor(string engine) => engine switch
    {
        Interim => TerminalHarnessPaths.AllowlistFile,
        Libvterm => Path.Combine(TerminalHarnessPaths.TerminalDir, "known-failures.libvterm.txt"),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown terminal engine."),
    };

    /// <summary>Golden path per engine: the interim goldens keep their P2-04 names (never rewritten
    /// by P2-18 — that is the no-golden-regression gate); libvterm goldens sit beside them with an
    /// engine suffix.</summary>
    public static string GoldenPath(string transcript, string engine) => engine == Interim
        ? Path.Combine(TerminalHarnessPaths.TranscriptsDir, transcript + ".golden")
        : Path.Combine(TerminalHarnessPaths.TranscriptsDir, transcript + "." + engine + ".golden");

    /// <summary>The engine's input-side paste encoder (coverage matrix input rows).</summary>
    public static byte[]? EncodePaste(string engine, string text, bool bracketedPasteActive) => engine switch
    {
        Interim => InterimInputEncoder.EncodePaste(text, bracketedPasteActive),
        Libvterm => GridInputEncoder.EncodePaste(text, bracketedPasteActive),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown terminal engine."),
    };

    /// <summary>The engine's input-side mouse encoder (coverage matrix input rows).</summary>
    public static byte[]? EncodeMouseClick(string engine, int button, int col, int row, bool sgr) => engine switch
    {
        Interim => InterimInputEncoder.EncodeMouseClick(button, col, row, sgr),
        Libvterm => GridInputEncoder.EncodeMousePress(button, col, row, sgr),
        _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, "Unknown terminal engine."),
    };
}

/// <summary>Keeps the engine roster honest: where the environment requires the libvterm legs
/// (CI), their absence is a failure, never a silent skip.</summary>
public sealed class EngineCatalogTests
{
    [Fact]
    public void Libvterm_IsAvailable_WhereRequired()
    {
        if (EngineCatalog.LibvtermRequired)
        {
            Assert.Contains(EngineCatalog.Libvterm, EngineCatalog.AvailableEngines);
        }
    }
}
