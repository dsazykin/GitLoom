using System;
using System.Collections.Generic;
using System.IO;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The version-anchor guard: recompute each jail image's source hash from <c>images/&lt;name&gt;/</c>
/// and assert it equals the committed <see cref="SandboxImageVersions"/> constant. This is what keeps
/// the constant honest — edit a Dockerfile (or a COPY'd script) without updating the constant and
/// this test fails, printing the new hash to paste. The constant is what the app's staleness probe
/// and the daemon's spawn preflight compare against and what CI stamps as the image label, so a drift
/// here is exactly the skew class Item 1 set out to close.
/// </summary>
public class SandboxImageVersionsGuardTests
{
    [Fact]
    public void AgentBase_VersionConstant_MatchesImageSource() =>
        AssertConstantMatchesSource(
            SandboxImageVersions.AgentBaseName,
            SandboxImageSourceHasher.AgentBaseInputs,
            SandboxImageVersions.AgentBase);

    [Fact]
    public void EgressProxy_VersionConstant_MatchesImageSource() =>
        AssertConstantMatchesSource(
            SandboxImageVersions.EgressProxyName,
            SandboxImageSourceHasher.EgressProxyInputs,
            SandboxImageVersions.EgressProxy);

    private static void AssertConstantMatchesSource(
        string imageName, IReadOnlyList<string> curatedInputs, string committedConstant)
    {
        var imageDir = Path.Combine(RepoImagesRoot(), imageName);
        var computed = SandboxImageSourceHasher.HashInputs(imageDir, curatedInputs);

        Assert.True(
            string.Equals(computed, committedConstant, StringComparison.Ordinal),
            $"SandboxImageVersions.{imageName} is stale.\n"
            + $"  images/{imageName}/ now hashes to : {computed}\n"
            + $"  committed constant is            : {committedConstant}\n"
            + "A jail-image build input changed. Paste the computed value into "
            + "Mainguard.Agents/Agents/Sandbox/SandboxImageVersions.cs AND bump the App/Server versions in "
            + "lockstep — the daemon compares this constant at spawn preflight, so it must ship in step "
            + "with the image the app builds/labels.");
    }

    /// <summary>Walks up from the test base directory to the repo's <c>images/</c> dir (the pattern the
    /// provider tests use) — robust to bin/Debug|Release depth and to running under a git worktree.</summary>
    private static string RepoImagesRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
               && !File.Exists(Path.Combine(dir, "images", SandboxImageVersions.AgentBaseName, "Dockerfile")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.True(dir is not null, "Could not locate the repo 'images/' directory by walking up from "
            + AppContext.BaseDirectory);
        return Path.Combine(dir!, "images");
    }
}
