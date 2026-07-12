using System.Threading.Tasks;
using GitLoom.Protos.V1;
using Grpc.Core;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="RepoSyncService"/>. The RPCs and their typed
/// <see cref="StatusCode.Unimplemented"/> stubs land in P2-02; the bodies (bare-mirror
/// provision, agent worktrees, quarantine remote) land in P2-06. The stable message
/// names below the stub keep P2-06 from being a proto break.
/// </summary>
public sealed class RepoSyncGrpcService : RepoSyncService.RepoSyncServiceBase
{
    private const string NotYet = "RepoSyncService is not implemented until P2-06.";

    public override Task<ProvisionRepoResponse> ProvisionRepo(ProvisionRepoRequest request, ServerCallContext context)
        => throw Unimplemented();

    public override Task<CreateWorktreeResponse> CreateWorktree(CreateWorktreeRequest request, ServerCallContext context)
        => throw Unimplemented();

    public override Task<ListWorktreesResponse> ListWorktrees(ListWorktreesRequest request, ServerCallContext context)
        => throw Unimplemented();

    public override Task<RemoveWorktreeResponse> RemoveWorktree(RemoveWorktreeRequest request, ServerCallContext context)
        => throw Unimplemented();

    private static RpcException Unimplemented() => new(new Status(StatusCode.Unimplemented, NotYet));
}
