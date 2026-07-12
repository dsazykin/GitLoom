using System;
using System.Threading;
using Docker.DotNet;
using Xunit;

namespace GitLoom.Server.Tests.Fixtures;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless a Docker daemon is reachable AND the CI-built P2-07
/// images are present (TI-P2-07 §A.5: the RequiresDocker leg is PR-blocking in Linux CI, where the
/// images are built first — <c>images/gitloom-agent-base</c> / <c>images/gitloom-egress-proxy</c>, never
/// at runtime, G-16 — but a developer machine without them skips rather than fails on an image pull).
/// The probe runs once and is cached. Apply the CI category with a class-level
/// <c>[Trait("Category","RequiresDocker")]</c> alongside this attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresDockerFactAttribute : FactAttribute
{
    public RequiresDockerFactAttribute()
    {
        if (!DockerAvailability.IsReady)
            Skip = DockerAvailability.SkipReason;
    }
}

internal static class DockerAvailability
{
    /// <summary>The agent base image the RequiresDocker leg needs (matches <c>SandboxFixture.ImageRef</c>).</summary>
    private static readonly string AgentImage =
        Environment.GetEnvironmentVariable("GITLOOM_AGENT_IMAGE") ?? "gitloom-agent-base:latest";

    private static readonly Lazy<(bool Ready, string Reason)> _probe = new(Probe);

    public static bool IsReady => _probe.Value.Ready;
    public static string SkipReason => _probe.Value.Reason;

    private static (bool, string) Probe()
    {
        try
        {
            using var client = new DockerClientConfiguration().CreateClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            client.System.PingAsync(cts.Token).GetAwaiter().GetResult();

            try
            {
                client.Images.InspectImageAsync(AgentImage, cts.Token).GetAwaiter().GetResult();
            }
            catch
            {
                return (false, $"Docker is up but the '{AgentImage}' image is not built (CI builds images/ first).");
            }

            return (true, string.Empty);
        }
        catch
        {
            return (false, "Docker daemon not reachable (RequiresDocker leg runs in Linux CI).");
        }
    }
}
