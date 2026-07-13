using System;

namespace GitLoom.Core.Agents;

/// <summary>
/// The WSL2 substrate implementation of <see cref="IAgentEnvironment"/>. Holds the real
/// P2-06 provisioner and worktree manager and resolves the host-side sync remote to a
/// <c>\\wsl.localhost\...</c> UNC handle. The <c>"gitloom-vm"</c> sync-remote name appears
/// in this method and NOWHERE else in the codebase (SC-2): every other layer registers
/// whatever <see cref="ResolveSyncRemote"/> returns, so the P2-25 cloud substrate can
/// resolve <c>gitloom-cloud</c> through the same seam.
/// </summary>
public sealed class Wsl2AgentEnvironment : IAgentEnvironment
{
    /// <summary>The default WSL2 sync-remote name (SC-2). Substrate-local by design.</summary>
    private const string Wsl2SyncRemoteName = "gitloom-vm";

    private readonly string _uncPrefix;

    /// <param name="vmRoot">The daemon-side ext4 base dir for mirrors/worktrees (defaults to <c>~/gitloom</c>).</param>
    /// <param name="userName">The Linux user whose home holds <c>gitloom/</c> (defaults to <c>USER</c>/<c>USERNAME</c>).</param>
    /// <param name="distroName">The WSL distro name in the UNC path (defaults to <c>GitLoomEnv</c>).</param>
    public Wsl2AgentEnvironment(string? vmRoot = null, string? userName = null, string? distroName = null)
    {
        var user = string.IsNullOrEmpty(userName)
            ? Environment.GetEnvironmentVariable("USER") ?? Environment.GetEnvironmentVariable("USERNAME") ?? "gitloom"
            : userName;
        var distro = string.IsNullOrEmpty(distroName) ? "GitLoomEnv" : distroName;

        // The Windows-facing UNC root of the VM's ~/<user>/gitloom/repos directory.
        _uncPrefix = $@"\\wsl.localhost\{distro}\home\{user}\gitloom\repos";

        // The provisioner's Windows-facing handle for a hash IS the resolved sync-remote URL.
        var provisioner = new RepoProvisioner(vmRoot, hash => ResolveSyncRemote(hash).Url);
        Repos = provisioner;
        Worktrees = new WorktreeManager(vmRoot);
    }

    public string SubstrateId => "wsl2";

    public SubstrateCapabilities Capabilities { get; } =
        new(SupportsMaxIsolationBackend: false, SupportsWarmPoolPrestart: false,
            FilesystemTransport: "9p", LifecycleDialect: "wsl");

    public IRepoProvisioner Repos { get; }

    public IAgentWorktreeManager Worktrees { get; }

    public SyncRemote ResolveSyncRemote(string repoHash)
        => new(Wsl2SyncRemoteName, $@"{_uncPrefix}\{repoHash}.git");
}
