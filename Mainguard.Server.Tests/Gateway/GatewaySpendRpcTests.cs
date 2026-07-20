using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests.Gateway;

/// <summary>
/// P2-08 test contract #8 — budgets get/set persist, and spend rows stream over
/// <c>GatewayService.StreamSpend</c> with snapshot totals reconciling against the streamed rows. Runs
/// against the in-proc daemon (<see cref="DaemonFixture"/>).
/// </summary>
public class GatewaySpendRpcTests
{
    [Fact]
    public async Task SetAndGetBudgets_RoundTripsThroughDaemon()
    {
        using var fixture = new DaemonFixture();
        var client = new GatewayService.GatewayServiceClient(fixture.CreateChannel());

        await client.SetBudgetsAsync(
            new SetBudgetsRequest { Budget = new Budget { TokenCap = 5000, UsdMicrosCap = 1_000_000 } },
            fixture.AuthHeaders());

        var got = await client.GetBudgetsAsync(new GetBudgetsRequest(), fixture.AuthHeaders());
        Assert.Equal(5000, got.Budget.TokenCap);
        Assert.Equal(1_000_000, got.Budget.UsdMicrosCap);

        // The live ledger reflects the persisted caps.
        var ledger = fixture.Services.GetRequiredService<BudgetLedger>();
        Assert.Equal(5000, ledger.Caps.PerAgentTokenCap);
    }

    [Fact]
    public async Task StreamSpend_StreamsLedgerRows_SnapshotTotalsMatch()
    {
        using var fixture = new DaemonFixture();

        // Isolate the spend stores to fresh in-memory instances so the replay stream sees only this
        // test's rows (the daemon DB path is shared across in-proc hosts in the WAF test tier).
        using var isolated = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddSingleton<ISpendStore>(new InMemorySpendStore());
            services.AddSingleton<IExpectedAgentStore>(new InMemoryExpectedAgentStore());
            services.AddSingleton<IBudgetStore>(new InMemoryBudgetStore());
        }));

        var client = new GatewayService.GatewayServiceClient(
            GrpcChannel.ForAddress(isolated.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = isolated.Server.CreateHandler() }));
        var token = isolated.Services.GetRequiredService<Mainguard.Server.Auth.SessionTokenFile>().Token;
        var headers = new Grpc.Core.Metadata { { "authorization", $"bearer {token}" } };
        var ledger = isolated.Services.GetRequiredService<BudgetLedger>();
        var gateway = isolated.Services.GetRequiredService<AiGateway>();

        // Record two settled spend rows through the shared ledger; the stream replays existing rows on
        // subscribe, so they arrive deterministically regardless of subscription timing.
        ledger.Record("agent-a", "gpt-4o", 100);
        ledger.Record("agent-b", "gpt-4o-mini", 200);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var call = client.StreamSpend(new StreamSpendRequest(), headers, cancellationToken: cts.Token);

        var received = new List<SpendSample>();
        while (received.Count < 2 && await call.ResponseStream.MoveNext(cts.Token))
        {
            received.Add(call.ResponseStream.Current);
        }

        Assert.Equal(2, received.Count);
        Assert.Equal(300, received.Sum(r => r.TokensSpent));
        Assert.Contains(received, r => r.AgentId == "agent-a" && r.TokensSpent == 100);
        Assert.Contains(received, r => r.AgentId == "agent-b" && r.Model == "gpt-4o-mini");

        // Snapshot totals reconcile with the streamed rows.
        var snapshot = gateway.GetSnapshot();
        Assert.Equal(
            received.Sum(r => r.TokensSpent),
            snapshot.Agents.Where(a => a.AgentId is "agent-a" or "agent-b").Sum(a => a.Tokens));

        cts.Cancel(); // end the server stream
    }
}
