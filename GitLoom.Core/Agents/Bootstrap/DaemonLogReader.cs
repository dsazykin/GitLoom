using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Read-only tail of the in-VM daemon logs for the App's "Daemon logs" settings panel, over the same
/// <see cref="IWslRunner"/> seam <c>WslDaemonHealthProbe</c> already uses for the OOBE error card. Two
/// sources: the unified systemd journal (<c>journalctl -u gitloomd</c>, all subsystems interleaved) and
/// the per-subsystem rolling files under <c>~/.gitloom/logs/&lt;subsystem&gt;.log</c>.
///
/// <para><b>Never throws.</b> A non-zero exit or a WSL failure returns an empty string, so a read
/// surface renders "nothing to show" rather than faulting — diagnostics must never break the surface
/// that reads them.</para>
/// </summary>
public sealed class DaemonLogReader
{
    /// <summary>The VM service user's logs directory: <c>gitloomd</c> runs as uid 1000 with
    /// <c>HOME=/home/gitloom</c>, so its logs sit at <c>/home/gitloom/.gitloom/logs</c> regardless of
    /// the Windows-side data root the App uses.</summary>
    public const string VmLogsDir = "/home/gitloom/.gitloom/logs";

    private readonly IWslRunner _wsl;

    public DaemonLogReader(IWslRunner wsl) => _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));

    /// <summary>The unified daemon journal tail (every subsystem interleaved), oldest→newest.</summary>
    public Task<string> ReadRecentAsync(int lines, CancellationToken ct = default) =>
        RunAsync(WslCommands.InDistroAsRoot(
            "journalctl", "-u", "gitloomd", "--no-pager", "-n", Clamp(lines).ToString(), "-o", "cat"), ct);

    /// <summary>One subsystem's rolling file tail (e.g. <c>spawn.log</c>), oldest→newest.</summary>
    public Task<string> ReadSubsystemAsync(string subsystem, int lines, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subsystem))
            return Task.FromResult(string.Empty);

        var file = $"{VmLogsDir}/{SanitizeSubsystem(subsystem)}.log";
        return RunAsync(WslCommands.InDistroAsRoot("tail", "-n", Clamp(lines).ToString(), file), ct);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var result = await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
            // A missing file (tail: no such file) or a stopped VM is "nothing to show", not an error.
            return result.Succeeded ? result.StdOut : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static int Clamp(int lines) => lines < 1 ? 1 : (lines > 5000 ? 5000 : lines);

    // The subsystem name comes from our own fixed list, but never let a stray path separator or space
    // compose a different argument: keep only lowercased alphanumerics (the canonical names are exactly
    // that). Empty input degrades to the always-present lifecycle log.
    private static string SanitizeSubsystem(string subsystem)
    {
        var sb = new StringBuilder(subsystem.Length);
        foreach (var c in subsystem)
        {
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        }

        return sb.Length > 0 ? sb.ToString() : "lifecycle";
    }
}
