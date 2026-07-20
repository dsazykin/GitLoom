using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>Result of a single <c>wsl.exe</c> invocation.</summary>
public readonly record struct WslRunResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Seam over <c>wsl.exe</c> so the bootstrap steps are unit-testable without a real WSL install.
/// Implementations MUST pass arguments as an argument list (never a concatenated command string,
/// and never via an intermediate OS shell).
/// </summary>
public interface IWslRunner
{
    /// <summary>
    /// Runs <c>wsl.exe</c> with the given argument list. <paramref name="stdin"/> is written to the
    /// child's standard input when non-null (used to feed file content to <c>tee</c> without a shell
    /// redirect). Throws <see cref="WslNotInstalledException"/> when <c>wsl.exe</c> is absent.
    /// </summary>
    Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct);
}

/// <summary>
/// Pure builders for the <c>wsl.exe</c> argument lists the bootstrapper uses. Kept separate from the
/// runner so the command shapes — and the G-12 invariant that <b>no builder ever emits the VM-wide
/// shutdown verb</b> — are unit-testable without a process. Lifecycle verbs are scoped to our own
/// distro only: <c>--terminate MainguardEnv</c> → poll → <c>--unregister MainguardEnv</c>.
///
/// <para><b>Phase-4 re-register migration:</b> the distro was renamed <c>GitLoomEnv → MainguardEnv</c>.
/// An upgrading install re-registers its existing distro under the new name —
/// <c>--export GitLoomEnv</c> → <c>--import MainguardEnv</c> → <c>--unregister GitLoomEnv</c> — all
/// scoped to the two named distros, NEVER the VM-wide shutdown verb (G-12). The <see cref="LegacyDistroName"/>
/// builders below drive that; fresh installs never touch them and provision <see cref="DistroName"/> directly.</para>
/// </summary>
public static class WslCommands
{
    /// <summary>The dedicated distro Mainguard provisions; the user's own distros are never touched.</summary>
    public const string DistroName = "MainguardEnv";

    /// <summary>The pre-rebrand distro name. Present ONLY so the upgrade path can detect and re-register
    /// a legacy install as <see cref="DistroName"/> (export → import → unregister); never provisioned anew.</summary>
    public const string LegacyDistroName = "GitLoomEnv";

    public static IReadOnlyList<string> ListQuiet() => new[] { "--list", "--quiet" };

    /// <summary>The running distros only — used by the uninstaller to poll that <c>MainguardEnv</c> has
    /// stopped after <see cref="Terminate"/> before it unregisters (and to confirm G-12: the diff
    /// against this list proves personal distros were never stopped).</summary>
    public static IReadOnlyList<string> ListRunning() => new[] { "--list", "--running", "--quiet" };

    public static IReadOnlyList<string> Import(string installDir, string tarballPath) =>
        new[] { "--import", DistroName, installDir, tarballPath, "--version", "2" };

    /// <summary>G-12: terminate <b>our</b> distro only. Never the VM-wide shutdown verb (that would
    /// kill the user's personal distros too).</summary>
    public static IReadOnlyList<string> Terminate() => new[] { "--terminate", DistroName };

    /// <summary>Remove a half-imported / retired distro. Scoped to <c>MainguardEnv</c> only.</summary>
    public static IReadOnlyList<string> Unregister() => new[] { "--unregister", DistroName };

    /// <summary>Phase-4 upgrade: export the legacy <c>GitLoomEnv</c> rootfs to <paramref name="exportPath"/>
    /// so it can be re-imported under <see cref="DistroName"/>. Scoped to the legacy distro only.</summary>
    public static IReadOnlyList<string> ExportLegacy(string exportPath) =>
        new[] { "--export", LegacyDistroName, exportPath };

    /// <summary>Phase-4 upgrade: import the exported legacy rootfs under the NEW <see cref="DistroName"/>.
    /// Same shape as <see cref="Import"/> — the re-register just points a fresh install dir at the export.</summary>
    public static IReadOnlyList<string> ImportMigrated(string installDir, string exportPath) =>
        Import(installDir, exportPath);

    /// <summary>Phase-4 upgrade: unregister the legacy <c>GitLoomEnv</c> distro AFTER it has been
    /// re-imported as <see cref="DistroName"/>. Scoped to the legacy distro only (G-12: never the
    /// VM-wide shutdown verb).</summary>
    public static IReadOnlyList<string> UnregisterLegacy() =>
        new[] { "--unregister", LegacyDistroName };

    /// <summary>An in-distro command: <c>wsl -d MainguardEnv -- &lt;cmd...&gt;</c>.</summary>
    public static IReadOnlyList<string> InDistro(params string[] command) =>
        new[] { "-d", DistroName, "--" }.Concat(command).ToArray();

    /// <summary>An in-distro command as root: <c>wsl -d MainguardEnv -u root -- &lt;cmd...&gt;</c>.</summary>
    public static IReadOnlyList<string> InDistroAsRoot(params string[] command) =>
        new[] { "-d", DistroName, "-u", "root", "--" }.Concat(command).ToArray();

    /// <summary>Every lifecycle/command builder — used by the G-12 unit test to prove none emit the
    /// VM-wide shutdown verb for any state.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBuilders() => new[]
    {
        ListQuiet(),
        ListRunning(),
        Import(@"C:\Mainguard\vm", @"C:\Mainguard\mainguardos.tar.gz"),
        Terminate(),
        Unregister(),
        ExportLegacy(@"C:\Mainguard\legacy-export.tar"),
        ImportMigrated(@"C:\Mainguard\vm", @"C:\Mainguard\legacy-export.tar"),
        UnregisterLegacy(),
        InDistro("true"),
        InDistroAsRoot("true"),
    };
}

/// <summary>
/// Hardened <c>wsl.exe</c> invoker (P2-05). Uses <see cref="ProcessStartInfo.ArgumentList"/> so there
/// is no shell quoting/injection surface, and decodes stdout as UTF-16LE — <c>wsl.exe</c> emits its
/// own output (notably <c>--list --quiet</c>) as UTF-16LE, and byte-level distro-name parsing breaks
/// under UTF-8. NUL padding and a BOM are stripped defensively.
/// </summary>
public sealed class WslRunner : IWslRunner
{
    private readonly string _executable;

    public WslRunner(string executable = "wsl.exe")
    {
        _executable = executable;
    }

    public async Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);

        var psi = new ProcessStartInfo
        {
            FileName = _executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            // P2-48: windowless — no console flash when wsl.exe runs mid-OOBE (e.g. --import).
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // wsl.exe defaults to UTF-16LE for its OWN messages (e.g. --list), but relays the output of
            // an in-distro command (`wsl -d X -- cmd`) as that program's raw bytes — UTF-8 on Linux. One
            // fixed decoder can't read both, so decoding everything as UTF-16 turned in-distro errors
            // (e.g. `docker info` stderr) into mojibake. WSL_UTF8=1 makes wsl.exe emit its own text as
            // UTF-8 too, so the whole stream is uniformly UTF-8 and decodes cleanly.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["WSL_UTF8"] = "1";
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new WslNotInstalledException();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // wsl.exe not on PATH (WSL not enabled) surfaces as a Win32Exception — the actionable,
            // terminal signal that enablement (P2-21) has not been run. Never attempt install here.
            throw new WslNotInstalledException(ex);
        }

        using (process)
        using (ct.Register(() => { try { process.Kill(true); } catch { /* already exited */ } }))
        {
            // Start draining stdout/stderr BEFORE writing stdin. An in-distro `tee` (the file-write path
            // used by adapter staging and config shims) echoes its stdin straight back to stdout, so a
            // large payload — staging streams base64 of a multi-MB CLI tarball — fills the child's ~64KB
            // stdout pipe buffer. The old order (write ALL of stdin, THEN read stdout) then deadlocked:
            // tee blocks writing stdout, stops reading stdin, our write blocks on the full stdin buffer,
            // and neither side moves until ct kills the process — which surfaced to the user as
            // "tee exit -1" / "the pipe is being closed". Draining concurrently is the standard fix.
            // (Latent until adapters shipped: every earlier caller fed tiny stdin — sysctl drop-ins,
            // small config files — that fit whole inside one pipe buffer.)
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            if (stdin != null)
            {
                try
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The child closed its stdin early (e.g. it exited before consuming everything). Its
                    // exit code and stderr are the meaningful diagnosis — not a write-side "pipe closed" —
                    // so swallow this and let WaitForExit + the drained streams below report the real cause.
                }
            }

            try { process.StandardInput.Close(); }
            catch (IOException) { /* stdin already torn down with the process */ }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var stdout = Normalize(await stdoutTask.ConfigureAwait(false));
            var stderr = Normalize(await stderrTask.ConfigureAwait(false));
            return new WslRunResult(process.ExitCode, stdout, stderr);
        }
    }

    // Defensively strips a BOM and any interleaved NUL characters that can survive in captured output.
    private static string Normalize(string raw) =>
        string.IsNullOrEmpty(raw) ? raw : raw.Replace("﻿", "", StringComparison.Ordinal).Replace("\0", "", StringComparison.Ordinal);

    /// <summary>
    /// Parses the newline-separated distro names from <c>wsl --list --quiet</c> output (UTF-8-decoded
    /// via WSL_UTF8). Defensively strips a BOM and NUL padding, trims each line, and drops blanks.
    /// </summary>
    public static IReadOnlyList<string> ParseDistroList(string listOutput)
    {
        if (string.IsNullOrEmpty(listOutput))
            return Array.Empty<string>();

        var cleaned = listOutput.Replace("﻿", "", StringComparison.Ordinal).Replace("\0", "", StringComparison.Ordinal);
        return cleaned
            .Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();
    }
}
