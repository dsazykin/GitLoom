using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using Mainguard.Git.Models;
using GitLoom.Protos.V1;
using Grpc.Core;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="GatewayService"/> (P2-08). Validation + dispatch only — the budget
/// caps live in the <see cref="IBudgetStore"/> (persisted) and the live <see cref="BudgetLedger"/>,
/// and the spend stream is the ledger's row feed. The proto <c>Budget</c> maps to the per-agent
/// token + USD-micro caps.
/// </summary>
public sealed class GatewayGrpcService : GatewayService.GatewayServiceBase
{
    private readonly BudgetLedger _ledger;
    private readonly IBudgetStore _budgetStore;

    public GatewayGrpcService(BudgetLedger ledger, IBudgetStore budgetStore)
    {
        _ledger = ledger;
        _budgetStore = budgetStore;
    }

    public override Task<GetBudgetsResponse> GetBudgets(GetBudgetsRequest request, ServerCallContext context)
    {
        var stored = _budgetStore.Get();
        return Task.FromResult(new GetBudgetsResponse { Budget = ToProto(stored) });
    }

    public override Task<SetBudgetsResponse> SetBudgets(SetBudgetsRequest request, ServerCallContext context)
    {
        var incoming = request.Budget ?? new Budget();
        var stored = _budgetStore.Set(
            incoming.UsdMicrosCap, incoming.TokenCap,
            incoming.UsdMicrosCapPerDay, incoming.TokenCapPerDay);

        // Reflect the persisted caps in the live ledger — per-agent AND per-day (the daemon already
        // enforces both in BudgetCaps; P2-13 carried-in from P2-08 makes per-day editable over gRPC).
        _ledger.Caps = new BudgetCaps(
            PerAgentTokenCap: stored.TokenCap,
            PerAgentUsdMicrosCap: stored.UsdMicrosCap,
            PerDayTokenCap: stored.TokenCapPerDay,
            PerDayUsdMicrosCap: stored.UsdMicrosCapPerDay);

        return Task.FromResult(new SetBudgetsResponse { Budget = ToProto(stored) });
    }

    private static Budget ToProto(Mainguard.Git.Models.GatewayBudget stored) => new()
    {
        UsdMicrosCap = stored.UsdMicrosCap,
        TokenCap = stored.TokenCap,
        UsdMicrosCapPerDay = stored.UsdMicrosCapPerDay,
        TokenCapPerDay = stored.TokenCapPerDay,
    };

    public override async Task StreamSpend(
        StreamSpendRequest request,
        IServerStreamWriter<SpendSample> responseStream,
        ServerCallContext context)
    {
        // Bridge the ledger's row feed to the server stream: replay existing rows, then live rows,
        // until the client detaches.
        var channel = Channel.CreateUnbounded<SpendRecord>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        void OnRecorded(SpendRecord row) => channel.Writer.TryWrite(row);

        // Replay first so a late subscriber still sees the full ledger, then subscribe to new rows.
        foreach (var row in _ledger.AllRows())
        {
            channel.Writer.TryWrite(row);
        }

        _ledger.SpendRecorded += OnRecorded;
        try
        {
            await foreach (var row in channel.Reader.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(Map(row));
            }
        }
        catch (System.OperationCanceledException)
        {
            // Client detached — normal stream teardown.
        }
        finally
        {
            _ledger.SpendRecorded -= OnRecorded;
            channel.Writer.TryComplete();
        }
    }

    private static SpendSample Map(SpendRecord row) => new()
    {
        UsdMicrosSpent = row.UsdMicros,
        TokensSpent = row.Tokens,
        AgentId = row.AgentId,
        Model = row.Model,
    };
}
