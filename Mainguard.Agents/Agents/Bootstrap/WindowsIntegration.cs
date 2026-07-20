using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Pure <c>reg.exe</c> argument-list builders for Mainguard's Windows shell integration (P2-22 §J-4): the
/// Explorer "Open in Mainguard" context menu and the <c>mainguard://</c> protocol handler. Kept pure so the
/// exact keys written by install and removed by uninstall are unit-testable without a registry, and so
/// the <b>per-user invariant</b> is enforced by a test: every key lives under <c>HKCU\Software\Classes</c>
/// — a machine-wide (HKLM) write in a per-user install is a rejection trigger. The <see cref="ClassesRoot"/>
/// override lets the WindowsOnly round-trip test write under a disposable test key.
/// </summary>
public static class WindowsIntegration
{
    /// <summary>The default per-user classes root. Never HKLM in a per-user install.</summary>
    public const string HkcuClasses = @"HKCU\Software\Classes";

    /// <summary>The protocol scheme Mainguard registers for non-secret deep links.</summary>
    public const string ProtocolScheme = "mainguard";

    /// <summary>The verb key Mainguard writes under the Directory shell trees.</summary>
    public const string ContextMenuVerb = "Mainguard";

    /// <summary>The <c>reg.exe</c> add/delete argument lists that WRITE the integration. Each is a full
    /// argument list for <c>reg.exe</c> (the first element is <c>add</c>).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> InstallCommands(string exePath, string classesRoot = HkcuClasses)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var quotedExe = $"\"{exePath}\"";
        var cmds = new List<IReadOnlyList<string>>();

        // Explorer context menu on folders and on folder background.
        foreach (var basePath in new[]
        {
            $@"{classesRoot}\Directory\shell\{ContextMenuVerb}",
            $@"{classesRoot}\Directory\Background\shell\{ContextMenuVerb}",
        })
        {
            cmds.Add(RegAddDefault(basePath, "Open in Mainguard"));
            cmds.Add(RegAddValue(basePath, "Icon", quotedExe));
            cmds.Add(RegAddDefault($@"{basePath}\command", $"{quotedExe} \"%V\""));
        }

        // mainguard:// protocol handler (non-secret deep links only; DeepLinkParser guards the payload).
        var proto = $@"{classesRoot}\{ProtocolScheme}";
        cmds.Add(RegAddDefault(proto, "URL:Mainguard Protocol"));
        cmds.Add(RegAddValue(proto, "URL Protocol", string.Empty));
        cmds.Add(RegAddDefault($@"{proto}\shell\open\command", $"{quotedExe} \"%1\""));

        return cmds;
    }

    /// <summary>The <c>reg.exe</c> delete argument lists that REMOVE everything install wrote (tree
    /// deletes of the three top keys). Uninstall runs these; the round-trip test proves symmetry.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> UninstallCommands(string classesRoot = HkcuClasses) => new[]
    {
        RegDeleteTree($@"{classesRoot}\Directory\shell\{ContextMenuVerb}"),
        RegDeleteTree($@"{classesRoot}\Directory\Background\shell\{ContextMenuVerb}"),
        RegDeleteTree($@"{classesRoot}\{ProtocolScheme}"),
    };

    /// <summary>Every key path this integration owns — the uninstall symmetry + per-user-only tests read it.</summary>
    public static IReadOnlyList<string> OwnedKeyRoots(string classesRoot = HkcuClasses) => new[]
    {
        $@"{classesRoot}\Directory\shell\{ContextMenuVerb}",
        $@"{classesRoot}\Directory\Background\shell\{ContextMenuVerb}",
        $@"{classesRoot}\{ProtocolScheme}",
    };

    private static IReadOnlyList<string> RegAddDefault(string key, string data) =>
        new[] { "add", key, "/ve", "/t", "REG_SZ", "/d", data, "/f" };

    private static IReadOnlyList<string> RegAddValue(string key, string name, string data) =>
        new[] { "add", key, "/v", name, "/t", "REG_SZ", "/d", data, "/f" };

    private static IReadOnlyList<string> RegDeleteTree(string key) =>
        new[] { "delete", key, "/f" };
}

/// <summary>Runs a <c>reg.exe</c> argument list (Windows install matrix). Windowless — no console flash.
/// Non-Windows is a no-op so the uninstall/registration flows compile and run cross-platform in tests.</summary>
public interface IRegistryCommandRunner
{
    Task<bool> RunAsync(IReadOnlyList<string> regArgs, CancellationToken ct);
}

/// <summary>The real <c>reg.exe</c> runner. Mirrors the schtasks pattern (no Windows-only NuGet ref).</summary>
public sealed class RegExeRegistryCommandRunner : IRegistryCommandRunner
{
    public async Task<bool> RunAsync(IReadOnlyList<string> regArgs, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        var psi = new ProcessStartInfo
        {
            FileName = "reg.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in regArgs)
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;
            // Redirected pipes must be drained or a chatty reg.exe can fill one and never exit.
            var drainOut = p.StandardOutput.ReadToEndAsync(ct);
            var drainErr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(drainOut, drainErr).ConfigureAwait(false);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
