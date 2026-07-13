using System.Threading.Tasks;
using GitLoom.Protos.V1;
using Grpc.Core;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="GatewayService"/>. The RPCs and their typed
/// <see cref="StatusCode.Unimplemented"/> stubs land in P2-02; the token-bucket /
/// budget / backoff bodies land in P2-08. The stable message names keep P2-08 from
/// being a proto break.
/// </summary>
public sealed class GatewayGrpcService : GatewayService.GatewayServiceBase
{
    private const string NotYet = "GatewayService is not implemented until P2-08.";

    public override Task<GetBudgetsResponse> GetBudgets(GetBudgetsRequest request, ServerCallContext context)
        => throw Unimplemented();

    public override Task<SetBudgetsResponse> SetBudgets(SetBudgetsRequest request, ServerCallContext context)
        => throw Unimplemented();

    public override Task StreamSpend(
        StreamSpendRequest request,
        IServerStreamWriter<SpendSample> responseStream,
        ServerCallContext context)
        => throw Unimplemented();

    private static RpcException Unimplemented() => new(new Status(StatusCode.Unimplemented, NotYet));
}
