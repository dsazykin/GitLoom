using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Deterministic content hash of a jail image's CURATED build inputs — the version stamped as the
/// <see cref="SandboxImageVersions.LabelKey"/> label. Mirrors <c>build/mainguardos/build.sh:33</c>'s
/// "curated source → sha256 → stamp" pattern (an explicit input list, not a directory sweep) and
/// reuses <see cref="RepoPathHasher"/>'s hex style (<see cref="SHA256"/> + lowercase hex).
///
/// <para>The input lists are the files <c>docker build</c> actually consumes (the Dockerfile plus
/// every <c>COPY</c>'d file). README.md is documentation and <c>seccomp.json</c> is a RUNTIME input
/// (applied at container-create via <c>HostConfig.SecurityOpt</c>, embedded into Mainguard.Agents — see
/// <c>SeccompProfile</c>), never <c>COPY</c>'d into the image, so neither is a build input here.</para>
///
/// <para>Consumed ONLY by <c>SandboxImageVersionsGuardTests</c> (which recomputes from
/// <c>images/&lt;name&gt;/</c> and asserts the committed <see cref="SandboxImageVersions"/> constant
/// matches). The provisioner/app/daemon consume the committed constant, never this hasher.</para>
/// </summary>
public static class SandboxImageSourceHasher
{
    /// <summary>The agent-base image's curated build inputs.</summary>
    public static IReadOnlyList<string> AgentBaseInputs { get; } = new[] { "Dockerfile" };

    /// <summary>The egress-proxy image's curated build inputs (Dockerfile + its two COPY'd scripts).</summary>
    public static IReadOnlyList<string> EgressProxyInputs { get; } =
        new[] { "Dockerfile", "entrypoint.sh", "reload.sh" };

    /// <summary>
    /// The lowercase-hex SHA-256 over the sorted curated inputs: for each file (ordinal-sorted by its
    /// relative name) the UTF-8 relative name, a NUL separator, then the file's raw bytes — the same
    /// "<c>path\0content</c>, sorted" shape build.sh's <c>cat &lt;files&gt; | sha256sum</c> pins.
    /// Line endings are LF in the working tree (<c>.gitattributes eol=lf</c> for Dockerfile/*.sh), so
    /// the hash is byte-stable across a Windows checkout and the Linux CI leg.
    /// </summary>
    public static string HashInputs(string imageDirectory, IReadOnlyList<string> curatedInputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageDirectory);
        ArgumentNullException.ThrowIfNull(curatedInputs);

        using var sha = SHA256.Create();
        foreach (var relative in curatedInputs.OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(name, 0, name.Length, null, 0);
            sha.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);

            var content = File.ReadAllBytes(Path.Combine(imageDirectory, relative));
            sha.TransformBlock(content, 0, content.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}
