using System.Linq;
using System.Threading.Tasks;
using GitLoom.Protos.V1;
using GitLoom.Server.Auth;
using GitLoom.Server.Tests.Fixtures;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitLoom.Server.Tests;

/// <summary>
/// P2-14 test 11 (OPS SA-1/F2, PR-blocking) — the recorded plan approver is daemon-derived from the
/// authenticated connection, NEVER a client-supplied field. Two proofs: (a) the <c>ApprovePlanRequest</c>
/// proto has no identity/approver field at all, so a hand-crafted client cannot supply one; (b) the
/// recorded + echoed identity equals the daemon's peer-credential resolver value, independent of the
/// request.
/// </summary>
public class ApproverIdentityDaemonDerivedTests
{
    private sealed class FakeIdentityResolver : IApproverIdentityResolver
    {
        private readonly string _identity;
        public FakeIdentityResolver(string identity) => _identity = identity;
        public string Resolve(ServerCallContext context) => _identity;
    }

    [Fact]
    public void ApproverIdentity_IsDaemonDerived_NotClientField_ProtoHasNoIdentityField()
    {
        // The approval request carries ONLY plan_id — there is no client identity/approver/os field to set.
        var fieldNames = ApprovePlanRequest.Descriptor.Fields.InFieldNumberOrder().Select(f => f.Name).ToArray();

        Assert.Contains("plan_id", fieldNames);
        Assert.DoesNotContain(fieldNames, n =>
            n.Contains("identit") || n.Contains("approv") || n.Contains("os_") || n == "os" || n.Contains("uid"));
    }

    [Fact]
    public async Task ApproverIdentity_IsDaemonDerived_NotClientField_RecordsResolverValue()
    {
        using var fixture = new DaemonFixture();
        using var isolated = fixture.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            // Replace the peer-credential resolver with a deterministic daemon-side value.
            services.AddSingleton<IApproverIdentityResolver>(new FakeIdentityResolver("peer-uid-1000"));
        }));

        // Draft a pending plan directly on the daemon's service (the coordinator's spawn_worker lands here).
        var svc = isolated.Services.GetRequiredService<GitLoom.Core.Agents.Orchestrator.PlanApprovalService>();
        var fields = new GitLoom.Core.Agents.Orchestrator.TaskPlanFields(new[] { "src/a.cs" }, "approach", "tests");
        var draft = svc.Draft("coord-1", "Refactor", fields, "prompt", 1.5m);

        var token = isolated.Services.GetRequiredService<SessionTokenFile>().Token;
        var headers = new Metadata { { "authorization", $"bearer {token}" } };
        var client = new PlanApprovalService.PlanApprovalServiceClient(
            GrpcChannel.ForAddress(isolated.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = isolated.Server.CreateHandler() }));

        // Approve — the client sends only the plan id; it cannot influence the approver.
        var response = await client.ApprovePlanAsync(new ApprovePlanRequest { PlanId = draft.PlanId }, headers);

        Assert.True(response.Approved);
        Assert.Equal("peer-uid-1000", response.ApproverIdentity); // daemon-derived, echoed back

        // The persisted approval record carries the daemon-derived identity.
        var persisted = svc.Get(draft.PlanId!);
        Assert.NotNull(persisted);
        Assert.Equal("peer-uid-1000", persisted!.ApproverIdentity);
    }
}
