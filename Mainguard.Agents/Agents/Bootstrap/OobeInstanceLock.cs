using System;
using System.IO;

using Mainguard.Git;
namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Cross-process mutual exclusion for the OOBE state machine (P2-48 hardening).
///
/// <para><b>Why:</b> the reboot-resume Scheduled Task launches a second Mainguard process that drives
/// the SAME state machine over the SAME <c>oobe-state.json</c>/<c>elevated-result.json</c> files as an
/// interactively launched wizard. Two uncoordinated drivers racing those files was the root defect
/// behind the 2026-07-14 zombie-resume incident — even a correct task can race the GUI. Every pass of
/// the machine must hold this lock.</para>
///
/// <para><b>Why a lock FILE and not a named <c>Mutex</c>:</b> a <c>Mutex</c> is thread-affine — it
/// cannot be safely released across the <c>await</c> hops an OOBE pass makes — and named mutexes do
/// not exist on Unix, where the unit tests run. An exclusively opened file (<c>FileShare.None</c>)
/// has the identical cross-process semantics, is crash-safe (the OS closes the handle and releases
/// the lock when the process dies — no stale-lock cleanup logic), and guards the very directory the
/// shared state files live in.</para>
/// </summary>
public sealed class OobeInstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private OobeInstanceLock(FileStream stream) => _stream = stream;

    /// <summary>The default lock path: <c>oobe.lock</c> next to <c>oobe-state.json</c>.</summary>
    public static string DefaultPath() => Path.Combine(MainguardPaths.DataRoot(), "oobe.lock");

    /// <summary>
    /// Attempts to take the machine-wide OOBE lock. Returns the held lock, or null when another
    /// Mainguard process is already driving setup. Never blocks.
    /// </summary>
    public static OobeInstanceLock? TryAcquire(string? path = null)
    {
        var lockPath = path ?? DefaultPath();
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        try
        {
            var stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return new OobeInstanceLock(stream);
        }
        catch (IOException)
        {
            return null; // held by another process
        }
        catch (UnauthorizedAccessException)
        {
            return null; // e.g. an elevated holder's file, an unelevated prober
        }
    }

    public void Dispose() => _stream.Dispose();
}
