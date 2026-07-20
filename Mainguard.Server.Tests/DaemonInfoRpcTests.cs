using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Runtime;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// The tier-1 daemon fast-path skew probe: <c>AgentService.GetDaemonInfo</c> exercised in-proc
/// through the real composition root (auth coverage for the new method rides the
/// <c>DaemonAuthTests</c> reflect-every-method theory automatically). The daemon must name its own
/// assembly version and the MainguardOS payload stamp — and an absent stamp must be an empty string,
/// never a throw (a <c>--local-dev</c> daemon has no <c>/etc/mainguardos-release</c>).
/// </summary>
public sealed class DaemonInfoRpcTests
{
    [Fact]
    public async Task GetDaemonInfo_ReturnsAssemblyVersion_AndTheStampedPayloadVersion()
    {
        var releasePath = Path.Combine(
            Path.GetTempPath(), "mainguard-release-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(
            releasePath, "MAINGUARDOS_VERSION=1.2.3\nBUILD_INPUTS_HASH=abc\nDEBIAN_SNAPSHOT=20250601T000000Z\n");
        try
        {
            using var host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddSingleton(new DaemonInfoProvider(releasePath))));

            var response = await CallGetDaemonInfoAsync(host);

            var expectedVersion = typeof(DaemonInfoProvider).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
            Assert.Equal(expectedVersion, response.DaemonVersion);
            Assert.Equal("1.2.3", response.PayloadVersion);
        }
        finally
        {
            File.Delete(releasePath);
        }
    }

    [Fact]
    public async Task GetDaemonInfo_AbsentReleaseStamp_YieldsEmptyPayloadVersion()
    {
        var missing = Path.Combine(
            Path.GetTempPath(), "mainguard-release-missing-" + Guid.NewGuid().ToString("N"));
        using var host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
            services.AddSingleton(new DaemonInfoProvider(missing))));

        var response = await CallGetDaemonInfoAsync(host);

        Assert.False(string.IsNullOrEmpty(response.DaemonVersion));
        Assert.Equal(string.Empty, response.PayloadVersion);
    }

    [Theory]
    [InlineData("MAINGUARDOS_VERSION=0.1.0\nBUILD_INPUTS_HASH=x\n", "0.1.0")]
    [InlineData("BUILD_INPUTS_HASH=x\r\nMAINGUARDOS_VERSION=0.1.0\r\n", "0.1.0")] // CRLF-tolerant
    [InlineData("MAINGUARDOS_VERSION= 0.1.0 \n", "0.1.0")] // trimmed
    [InlineData("BUILD_INPUTS_HASH=x\n", "")] // key absent
    [InlineData("", "")]
    public void ParsePayloadVersion_ExtractsTheStampedValue(string content, string expected)
    {
        Assert.Equal(expected, DaemonInfoProvider.ParsePayloadVersion(content));
    }

    private static async Task<GetDaemonInfoResponse> CallGetDaemonInfoAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
    {
        var channel = GrpcChannel.ForAddress(host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var token = host.Services.GetRequiredService<SessionTokenFile>().Token;
        var headers = new Metadata { { "authorization", $"bearer {token}" } };

        var client = new AgentService.AgentServiceClient(channel);
        return await client.GetDaemonInfoAsync(
            new GetDaemonInfoRequest(), headers, deadline: DateTime.UtcNow.AddSeconds(10));
    }
}
