using System;
using System.IO;
using Mainguard.Git.Exceptions;

using Mainguard.Git;
namespace GitLoom.Core.Agents;

/// <summary>The result of provisioning a Windows repo into the VM's bare mirror.</summary>
/// <param name="RepoHash">The SHA-256 handle of the normalized source path (names the mirror).</param>
/// <param name="BareRepoPath">The daemon-side path of the bare mirror (never crosses the wire).</param>
/// <param name="VmRemoteUrl">
/// The opaque, Windows-facing handle (G-14) the host repo registers as its sync remote —
/// a UNC path in production, the local bare path under test.
/// </param>
public sealed record ProvisionResult(string RepoHash, string BareRepoPath, string VmRemoteUrl);

/// <summary>
/// P2-06 daemon service (no UI dependency). Provisions a bare ext4 mirror of the user's
/// Windows repo inside <c>GitLoomEnv</c> and keeps it current. First provision clones
/// <c>--bare</c>; subsequent provisions of the same hash fetch incrementally (no re-clone).
/// The mirror is hardened so a hostile agent cannot rewrite <c>main</c> or delete refs.
/// </summary>
public interface IRepoProvisioner
{
    /// <summary>Clone-or-fetch the bare mirror for a normalized Windows repo path.</summary>
    ProvisionResult Provision(string windowsRepoPathNormalized);

    /// <summary>The daemon-side bare-mirror path for a repo hash (exists only once the repo is provisioned).
    /// Used by the spawn path to gate real-sandbox launch on "is this repo provisioned?" and by the merge-diff
    /// bridge to locate the agent branch. Never crosses the wire (G-14).</summary>
    string BareRepoPathFor(string repoHash);
}

/// <inheritdoc cref="IRepoProvisioner"/>
public sealed class RepoProvisioner : IRepoProvisioner
{
    private readonly string _vmRoot;
    private readonly Func<string, string> _vmRemoteUrlResolver;

    /// <param name="vmRoot">
    /// The VM base directory holding <c>repos/</c> and <c>worktrees/</c>. Injected so tests
    /// point it at a temp dir; production defaults to <c>~/gitloom</c> resolved from HOME.
    /// </param>
    /// <param name="vmRemoteUrlResolver">
    /// Maps a repo hash to the Windows-facing sync-remote URL. Defaults to the local bare
    /// path (the natural under-test value); <c>Wsl2AgentEnvironment</c> injects the UNC form.
    /// </param>
    public RepoProvisioner(string? vmRoot = null, Func<string, string>? vmRemoteUrlResolver = null)
    {
        _vmRoot = vmRoot ?? DefaultVmRoot();
        _vmRemoteUrlResolver = vmRemoteUrlResolver ?? (_ => string.Empty);
    }

    /// <summary>The bare-mirror path for a hash: <c>&lt;vmRoot&gt;/repos/&lt;hash&gt;.git</c>.</summary>
    public string BareRepoPathFor(string repoHash)
        => Path.Combine(_vmRoot, "repos", repoHash + ".git");

    public ProvisionResult Provision(string windowsRepoPathNormalized)
    {
        if (string.IsNullOrWhiteSpace(windowsRepoPathNormalized))
        {
            throw new ArgumentException("A source repo path is required.", nameof(windowsRepoPathNormalized));
        }

        var hash = RepoPathHasher.Hash(windowsRepoPathNormalized);
        var barePath = BareRepoPathFor(hash);
        var reposDir = Path.GetDirectoryName(barePath)!;
        Directory.CreateDirectory(reposDir);

        // The hash names the mirror from the CALLER's (Windows) path; git, however, runs inside the
        // VM and must be handed the /mnt/<drive>/… form it can actually open. Translation is a pure,
        // identity-preserving view change — a Linux path (tests/CI) passes through untouched. An
        // untranslatable source (UNC) surfaces as the typed provisioning failure the RPC layer maps.
        string gitSourcePath;
        try
        {
            gitSourcePath = HostPathTranslator.ToDaemonOpenablePath(windowsRepoPathNormalized);
        }
        catch (ArgumentException ex)
        {
            throw new RepoProvisioningException(ex.Message, ex);
        }

        if (IsExistingBareMirror(barePath))
        {
            // Subsequent provision of the same hash: incremental fetch, never a re-clone
            // (edge row 1). Objects already on disk survive. Fetch from the source by EXPLICIT
            // URL rather than a configured remote: agent worktrees share the bare repo's config
            // and repurpose `origin` as their quarantine remote (→ the bare path), so a
            // configured source remote would be clobbered. The explicit default-branch refspec
            // guarantees the head actually advances (a plain fetch only moves FETCH_HEAD); a glob
            // `+refs/heads/*:refs/heads/*` would be refused because checked-out agent/* branches
            // match the destination pattern, whereas the default branch is never checked out.
            var defaultBranch = ResolveDefaultBranch(barePath);
            AgentGitCommand.Run(barePath, "fetch", gitSourcePath, $"+{defaultBranch}:{defaultBranch}");
        }
        else
        {
            // First provision — or the bare dir was manually deleted (edge row 4): clone clean.
            // 9P is acceptable for object transfer; only file *watching* over 9P is forbidden.
            if (Directory.Exists(barePath))
            {
                // A partial/corrupt directory: clear it so the clone lands on a clean path.
                Directory.Delete(barePath, recursive: true);
            }

            AgentGitCommand.Run(reposDir, "clone", "--bare", gitSourcePath, barePath);
            AgentGitCommand.Run(barePath, "config", "core.untrackedCache", "true");

            // Quarantine the mirror itself (§3.4): a hostile agent push can add to agent/* refs
            // but can neither rewrite main non-fast-forward nor delete refs in the mirror.
            AgentGitCommand.Run(barePath, "config", "receive.denyNonFastForwards", "true");
            AgentGitCommand.Run(barePath, "config", "receive.denyDeletes", "true");
        }

        var vmRemoteUrl = _vmRemoteUrlResolver(hash);
        if (string.IsNullOrEmpty(vmRemoteUrl))
        {
            // No resolver supplied (bare provisioner unit test): the local bare path is the handle.
            vmRemoteUrl = barePath;
        }

        return new ProvisionResult(hash, barePath, vmRemoteUrl);
    }

    // A directory is a usable bare mirror only if it has the git object store; a manually
    // emptied/deleted dir falls through to a clean re-clone (edge row 4).
    private static bool IsExistingBareMirror(string barePath)
        => Directory.Exists(barePath) && Directory.Exists(Path.Combine(barePath, "objects"));

    // The mirror's HEAD points at the source's default branch (main/master); fetch advances it.
    private static string ResolveDefaultBranch(string barePath)
    {
        if (AgentGitCommand.TryRun(barePath, out var output, "symbolic-ref", "--short", "HEAD") == 0)
        {
            var name = output.Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return "main";
    }

    // GitLoomPaths.HomeDirectory(), not the old `?? "."` fallback: a relative VM root silently
    // resolving against the daemon's CWD is exactly the class of bug that crash-looped gitloomd.
    // An unresolvable home now fails loudly with the systemd remedy named.
    private static string DefaultVmRoot()
        => Path.Combine(GitLoomPaths.HomeDirectory(), "gitloom");
}
