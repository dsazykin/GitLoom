using System.Threading.Tasks;
using GitLoom.Protos.V1;
using GitLoom.Server.Auth;
using GitLoom.Server.Tests.Fixtures;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitLoom.Server.Tests;

/// <summary>
/// P2-14 test 6 (TI-P2-14.7) — the coordinator role is interceptor-enforced, not convention. A connection
/// authenticated with a coordinator credential is denied the merge RPCs and the human-only plan-approval
/// RPCs with <see cref="StatusCode.PermissionDenied"/>. The operator credential is not denied.
/// </summary>
public class RoleInterceptorTests
{
    private const string CoordinatorToken = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public async Task RoleInterceptor_DeniesMergeToCoordinator()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var merge = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());
        var coordinatorHeaders = fixture.AuthHeaders(CoordinatorToken);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            merge.BeginMergeAsync(new BeginMergeRequest { RepoHandle = "repo", AgentId = "a" }, coordinatorHeaders).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);

        var ex2 = await Assert.ThrowsAsync<RpcException>(() =>
            merge.ConfirmMergeAsync(new ConfirmMergeRequest { RepoHandle = "repo", AgentId = "a" }, coordinatorHeaders).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex2.StatusCode);
    }

    [Fact]
    public async Task RoleInterceptor_DeniesPlanApprovalToCoordinator()
    {
        using var fixture = new DaemonFixture();
        fixture.Services.GetRequiredService<ConnectionRoleRegistry>().RegisterCoordinatorToken(CoordinatorToken);

        var plans = new PlanApprovalService.PlanApprovalServiceClient(fixture.CreateChannel());

        // The coordinator cannot approve its OWN plans (self-approval is the threat).
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            plans.ApprovePlanAsync(new ApprovePlanRequest { PlanId = "any" }, fixture.AuthHeaders(CoordinatorToken)).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task RoleInterceptor_OperatorNotDenied_MergeRpcPasses()
    {
        using var fixture = new DaemonFixture();

        var merge = new MergeQueueService.MergeQueueServiceClient(fixture.CreateChannel());

        // The operator credential is not a coordinator — the role check passes. (No active queue for the
        // handle → NotFound, which proves the call was NOT blocked by PermissionDenied.)
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            merge.BeginMergeAsync(new BeginMergeRequest { RepoHandle = "no-such-repo", AgentId = "a" }, fixture.AuthHeaders()).ResponseAsync);
        Assert.NotEqual(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.Equal(StatusCode.NotFound, ex.StatusCode);
    }
}
