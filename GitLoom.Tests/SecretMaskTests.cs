using System.Linq;
using System.Reflection;
using GitLoom.App.Services;
using GitLoom.Protos.V1;

namespace GitLoom.Tests;

/// <summary>
/// Client-side thin twin of the G-13 mask: the client sends the credential through the
/// designated <c>// SECRET</c> proto field (which the server's SecretFieldMask redacts).
/// The "never logged" twin runs server-side in GitLoom.Server.Tests.LoggingMaskTests.
/// </summary>
public sealed class SecretMaskTests
{
    [Fact]
    public void SpawnAgentRequest_ShouldCarryModelApiKey_AtStableFieldNumber()
    {
        var descriptor = SpawnAgentRequest.Descriptor;
        var byName = descriptor.FindFieldByName("model_api_key");
        Assert.NotNull(byName);
        Assert.Equal(4, byName!.FieldNumber);
        Assert.Equal("model_api_key", descriptor.FindFieldByNumber(4).Name);
    }

    [Fact]
    public void DaemonClient_SpawnAgent_ShouldRouteCredentialThroughModelApiKeyParameter()
    {
        var method = typeof(DaemonClient).GetMethod(nameof(DaemonClient.SpawnAgentAsync), BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Contains(method!.GetParameters(), p => p.Name == "modelApiKey" && p.ParameterType == typeof(string));
    }
}
