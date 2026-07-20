using System;
using System.IO;
using System.Linq;
using Grpc.Net.Client;
using LibGit2Sharp;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-47 #7 — the new <c>MergeQueueService.GetMergeDiff</c> RPC, exercised in-proc through the REAL
/// composition root over a real provisioned bare mirror + agent worktree (a DualRepoFixture-style repo).
/// The daemon's <c>MergeBranchDiffService</c> runs <c>git diff main...agent/&lt;id&gt;</c> against the
/// mirror; the client parses the returned unified diff with the pure T-06 <c>PatchParser</c>; and the
/// review cockpit's <c>ReviewCockpitContext</c> builds cleanly from the parsed <c>FilePatch</c> list.
/// </summary>
public sealed class MergeDiffRpcTests
{
    [Fact]
    public void GetMergeDiff_OverRealRepo_ReturnsAgentBranchChanges_AndParsesToFilePatches()
    {
        var vmRoot = NewTempDir("mainguard-mergediff-vm-");
        var source = NewTempDir("mainguard-mergediff-src-");
        try
        {
            SeedRepo(source);

            // Provision the bare mirror and create the agent worktree the way P2-06 does, then land a
            // commit on the agent branch so main...agent/<id> has real changes to review.
            var provisioner = new RepoProvisioner(vmRoot);
            var worktrees = new WorktreeManager(vmRoot);
            var provision = provisioner.Provision(source);
            const string agentId = "agent-review-1";
            var worktreePath = worktrees.CreateAgentWorktree(provision.RepoHash, agentId);
            CommitInWorktree(worktreePath, "feature.cs", "public class Feature { }\n", "add feature");

            // Point the in-proc daemon's substrate at this temp mirror (the MergeBranchDiffService factory
            // resolves through the overridden IAgentEnvironment.Repos), then call the real RPC.
            using var host = new DaemonFixture().WithWebHostBuilder(b => b.ConfigureTestServices(services =>
                services.AddSingleton<IAgentEnvironment>(new Wsl2AgentEnvironment(vmRoot: vmRoot))));

            var channel = GrpcChannel.ForAddress(host.Server.BaseAddress,
                new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
            var token = host.Services.GetRequiredService<SessionTokenFile>().Token;
            var headers = new Grpc.Core.Metadata { { "authorization", $"bearer {token}" } };

            var client = new MergeQueueService.MergeQueueServiceClient(channel);
            var response = client.GetMergeDiff(
                new GetMergeDiffRequest { RepoHandle = provision.RepoHash, AgentId = agentId }, headers);

            Assert.Equal("agent/" + agentId, response.Branch);
            Assert.False(string.IsNullOrEmpty(response.MainBranch));
            Assert.Contains("feature.cs", response.UnifiedDiff);

            // The client-side parse the App uses: the unified diff → FilePatch list → ReviewCockpitContext.
            var files = PatchParser.Parse(response.UnifiedDiff);
            Assert.Single(files);
            Assert.Contains(files[0].Hunks.SelectMany(h => h.Lines),
                l => l.Kind == Mainguard.Git.Models.DiffLineKind.Add && l.Text.Contains("class Feature"));
        }
        finally
        {
            TryDelete(vmRoot);
            TryDelete(source);
        }
    }

    private static void SeedRepo(string path)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        repo.Config.Set("user.name", "test-user", ConfigurationLevel.Local);
        repo.Config.Set("user.email", "test@mainguard.local", ConfigurationLevel.Local);
        repo.Config.Set("core.autocrlf", false, ConfigurationLevel.Local);
        File.WriteAllText(Path.Combine(path, "README.md"), "seed\n");
        Commands.Stage(repo, "README.md");
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        repo.Commit("seed commit", sig, sig);
    }

    private static void CommitInWorktree(string worktreePath, string relPath, string content, string message)
    {
        File.WriteAllText(Path.Combine(worktreePath, relPath), content);
        using var repo = new Repository(worktreePath);
        Commands.Stage(repo, relPath);
        var sig = new Signature("test-user", "test@mainguard.local", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }

    private static string NewTempDir(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch { /* never fail a test from cleanup */ }
    }
}
