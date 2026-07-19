using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Clones a repository with live, monotonic progress and cancellation (T-21). A cancelled clone
/// deletes the partial destination directory before completing. Backed by LibGit2Sharp
/// <c>CloneOptions.FetchOptions.OnTransferProgress</c> / <c>OnCheckoutProgress</c>.
/// </summary>
public interface ICloneService
{
    /// <summary>
    /// Clones <paramref name="sourceUrl"/> into <paramref name="targetPath"/>.
    /// <list type="bullet">
    ///   <item>Reports <see cref="CloneProgress"/> (monotonic <c>ReceivedObjects</c>/<c>Percent</c>) through
    ///     <paramref name="progress"/>.</item>
    ///   <item>On cancellation (<paramref name="cancellationToken"/>) aborts the transfer, deletes the
    ///     partial destination, and throws <see cref="System.OperationCanceledException"/>.</item>
    ///   <item>Throws <see cref="Exceptions.GitOperationException"/> if the destination already exists and
    ///     is non-empty.</item>
    /// </list>
    /// </summary>
    /// <returns>The repository path libgit2 reports for the new clone (the <c>.git</c> directory under
    /// <paramref name="targetPath"/> for a normal working clone).</returns>
    Task<string> CloneAsync(
        string sourceUrl,
        string targetPath,
        System.IProgress<CloneProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
