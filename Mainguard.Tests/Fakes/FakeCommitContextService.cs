using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Tests.Fakes;

/// <summary>
/// Configurable <see cref="ICommitContextService"/> fake (TI-32) — no mocking library, matching the
/// zero-container philosophy. <see cref="IsSupported"/> and the per-commit lookup are backed by settable
/// delegates so a VM/render test can drive the gating and the returned PRs/linked issues deterministically.
/// </summary>
public sealed class FakeCommitContextService : ICommitContextService
{
    public Func<string, bool>? IsSupportedImpl { get; set; }
    public Func<string, string, CommitContextResult>? GetForCommitImpl { get; set; }

    public bool IsSupported(string repoPath) => (IsSupportedImpl ?? (_ => true))(repoPath);

    public Task<CommitContextResult> GetForCommitAsync(string repoPath, string sha, CancellationToken ct)
        => Task.FromResult((GetForCommitImpl ?? throw new NotSupportedException("GetForCommitImpl not set"))(repoPath, sha));
}
