using System;
using System.Collections.Generic;

namespace GitLoom.Core.Agents.Sandbox;

/// <summary>
/// The committed version anchor for each provisionable jail image: a content hash of that image's
/// curated build inputs (<see cref="SandboxImageSourceHasher"/>), stamped as the
/// <see cref="LabelKey"/> docker label at build time and checked at BOTH the app startup probe
/// (<c>SandboxImageProvisioner</c>) and the daemon spawn preflight (<c>SandboxAgentLauncher</c>). A
/// source change ⇒ a new hash here ⇒ the guard test (<c>SandboxImageVersionsGuardTests</c>) fails
/// until this constant is updated, which forces the deliberate lockstep App/Server version bump so
/// the daemon (which compares this value) always ships in step with the image the app builds.
///
/// <para>These strings are the SOURCE OF TRUTH the app, the daemon, and CI all reference (CI stamps
/// the same value as the label); the guard test keeps them honest against <c>images/&lt;name&gt;/</c>.
/// Do NOT hand-edit to a guessed value — run the guard test and paste the hash it prints.</para>
/// </summary>
public static class SandboxImageVersions
{
    /// <summary>The docker label every jail image carries; its value is the image's source hash.</summary>
    public const string LabelKey = "gitloom.image.version";

    /// <summary>The env var that overrides the agent-base image tag (dev/testing). Read through
    /// <see cref="AgentBaseRef"/> so the app builds/labels EXACTLY the tag the daemon preflights and
    /// spawns — closing the skew where the launcher honored the override but the provisioner didn't.</summary>
    public const string AgentImageOverrideEnvVar = "GITLOOM_AGENT_IMAGE";

    /// <summary>The untagged repository name of the hardened agent-base jail image.</summary>
    public const string AgentBaseName = "gitloom-agent-base";

    /// <summary>The untagged repository name of the default-deny egress-proxy image.</summary>
    public const string EgressProxyName = "gitloom-egress-proxy";

    /// <summary>Source hash of <c>images/gitloom-agent-base/</c> (curated input: Dockerfile).</summary>
    public const string AgentBase = "0bd293d26983ba8c29cff924ac8a694171c52574cbd11c78104da91a80294ebf";

    /// <summary>Source hash of <c>images/gitloom-egress-proxy/</c> (curated inputs: Dockerfile,
    /// entrypoint.sh, reload.sh).</summary>
    public const string EgressProxy = "6df9667e57469be492d2ba431b4fe5881bc9d0eecf56452d9a404c84f2b7a925";

    private static readonly IReadOnlyDictionary<string, string> ByName =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [AgentBaseName] = AgentBase,
            [EgressProxyName] = EgressProxy,
        };

    /// <summary>
    /// The expected source hash for <paramref name="imageNameOrRef"/>, keyed on the UNTAGGED image
    /// name so it survives a <c>GITLOOM_AGENT_IMAGE</c> tag override (both
    /// <c>gitloom-agent-base:latest</c> and <c>gitloom-agent-base:dev</c> resolve to the same
    /// version). Returns <c>null</c> for an image we never built — a fully-renamed override we
    /// cannot version-check — so the preflight then falls back to a presence-only check for it.
    /// </summary>
    public static string? For(string imageNameOrRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageNameOrRef);
        return ByName.TryGetValue(UntaggedName(imageNameOrRef), out var version) ? version : null;
    }

    /// <summary>
    /// The agent-base image ref the app builds/labels AND the daemon preflights/spawns — the single
    /// source that honors the <see cref="AgentImageOverrideEnvVar"/> override, defaulting to
    /// <c>gitloom-agent-base:latest</c>. <see cref="For"/> keys on the untagged name, so a tag
    /// override (<c>…:dev</c>) is still version-checked against <see cref="AgentBase"/>.
    /// </summary>
    public static string AgentBaseRef() =>
        AgentBaseRef(Environment.GetEnvironmentVariable(AgentImageOverrideEnvVar));

    /// <summary>Testable core of <see cref="AgentBaseRef()"/> — pins the override/default rule without
    /// mutating process env.</summary>
    internal static string AgentBaseRef(string? envOverride) =>
        string.IsNullOrEmpty(envOverride) ? AgentBaseName + ":latest" : envOverride;

    /// <summary>
    /// Strips a docker tag (<c>:tag</c>) and/or digest (<c>@sha256:…</c>) from an image reference,
    /// leaving the repository name — the key <see cref="For"/> looks up. A registry-port colon
    /// (<c>host:5000/name</c>) is preserved: only a colon AFTER the last <c>/</c> is a tag.
    /// </summary>
    internal static string UntaggedName(string imageNameOrRef)
    {
        var s = imageNameOrRef;

        var at = s.IndexOf('@');
        if (at >= 0)
        {
            s = s[..at];
        }

        var lastSlash = s.LastIndexOf('/');
        var lastColon = s.LastIndexOf(':');
        if (lastColon > lastSlash)
        {
            s = s[..lastColon];
        }

        return s;
    }
}
