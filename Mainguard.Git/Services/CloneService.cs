using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LibGit2Sharp;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Repository = LibGit2Sharp.Repository;

namespace Mainguard.Git.Services;

/// <summary>
/// <see cref="ICloneService"/> over LibGit2Sharp <see cref="Repository.Clone(string, string, CloneOptions)"/>.
/// Transfer/checkout callbacks drive a monotonic <see cref="CloneProgress"/>; cancellation is honoured
/// from inside the transfer callback (return false → libgit2 aborts) after which the partial destination
/// is deleted. For a private HTTPS clone, credentials come from the single-source
/// <see cref="CredentialResolver"/> so a token never enters the URL/argv/logs (G-4); the pinned libgit2
/// build has no SSH transport, so SSH-form clones are unsupported (as elsewhere in the app).
/// </summary>
public sealed class CloneService : ICloneService
{
    private readonly ISecureKeyring? _keyring;
    private readonly SshKeyService? _ssh;

    /// <summary>Default: anonymous clones (public/local). The app injects a keyring for private HTTPS clones.</summary>
    public CloneService() : this(null, null) { }

    public CloneService(ISecureKeyring? keyring, SshKeyService? ssh)
    {
        _keyring = keyring;
        _ssh = ssh;
    }

    public async Task<string> CloneAsync(
        string sourceUrl,
        string targetPath,
        IProgress<CloneProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
            throw new ArgumentException("Clone source URL is required.", nameof(sourceUrl));
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Clone destination path is required.", nameof(targetPath));

        // Clone-into-existing-non-empty-dir is a hard error (a fresh empty dir is fine — libgit2 fills it).
        if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
            throw new GitOperationException($"Destination '{targetPath}' already exists and is not empty.");

        int lastReceived = 0;
        int lastPercent = 0;
        bool cancelledViaCallback = false;

        void ReportPct(int pct) => lastPercent = Math.Max(lastPercent, pct);

        var options = new CloneOptions();

        options.FetchOptions.OnTransferProgress = tp =>
        {
            // Honour a token that was already cancelled before this call.
            if (cancellationToken.IsCancellationRequested) { cancelledViaCallback = true; return false; }

            // Monotonic received-objects; weight the receive phase to 0–90% of the bar.
            int received = Math.Max(lastReceived, tp.ReceivedObjects);
            lastReceived = received;
            int pct = tp.TotalObjects > 0 ? (int)(90L * received / tp.TotalObjects) : 0;
            ReportPct(pct);

            progress?.Report(new CloneProgress
            {
                Phase = ClonePhase.Receiving,
                ReceivedObjects = received,
                TotalObjects = tp.TotalObjects,
                IndexedObjects = tp.IndexedObjects,
                ReceivedBytes = tp.ReceivedBytes,
                Percent = lastPercent,
                StatusText = tp.TotalObjects > 0
                    ? $"Receiving objects {received}/{tp.TotalObjects}"
                    : "Receiving objects…",
            });

            // Re-check after reporting: a synchronous progress handler that cancels on first report
            // takes effect within this same callback, keeping the mid-clone cancel test deterministic.
            if (cancellationToken.IsCancellationRequested) { cancelledViaCallback = true; return false; }
            return true;
        };

        options.OnCheckoutProgress = (path, completed, total) =>
        {
            // Checkout is the final 90–100% band.
            int pct = total > 0 ? 90 + (int)(10L * completed / total) : 90;
            ReportPct(pct);
            progress?.Report(new CloneProgress
            {
                Phase = ClonePhase.CheckingOut,
                CheckoutStep = completed,
                TotalCheckoutSteps = total,
                ReceivedObjects = lastReceived,
                Percent = lastPercent,
                StatusText = total > 0 ? $"Checking out files {completed}/{total}" : "Checking out files…",
            });
        };

        // Credentials only for HTTPS token remotes; anonymous otherwise. The secret never leaves the
        // credentials object (never the URL/argv) — G-4.
        if (_keyring is not null && _ssh is not null)
        {
            options.FetchOptions.CredentialsProvider = (url, usernameFromUrl, types) =>
            {
                var resolved = CredentialResolver.Resolve(url, _keyring, _ssh);
                return resolved.Https ?? (Credentials)new DefaultCredentials();
            };
        }

        try
        {
            var resultPath = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Repository.Clone(sourceUrl, targetPath, options);
            }, cancellationToken).ConfigureAwait(false);

            progress?.Report(new CloneProgress
            {
                Phase = ClonePhase.Completed,
                ReceivedObjects = lastReceived,
                Percent = 100,
                StatusText = "Clone complete",
            });
            return resultPath;
        }
        catch (Exception ex) when (cancelledViaCallback
                                   || ex is OperationCanceledException
                                   || ex is UserCancelledException)
        {
            // Cancellation must not leave a partial repo on disk (T-21 invariant).
            DeleteDirectoryForce(targetPath);
            throw new OperationCanceledException("Clone was cancelled.", ex, cancellationToken);
        }
        catch (LibGit2SharpException ex)
        {
            // Surface as a typed git error; libgit2's own message is preserved for the UI.
            throw new GitOperationException($"Clone failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Recursively deletes a directory, clearing the read-only attribute libgit2 sets on packed objects
    /// (a plain <see cref="Directory.Delete(string, bool)"/> throws on those on Windows).
    /// </summary>
    private static void DeleteDirectoryForce(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best-effort */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; a locked handle on a background thread should not mask the cancel.
        }
    }
}
