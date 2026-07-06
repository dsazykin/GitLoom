using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.Tests.Fakes;

/// <summary>
/// Delegate-backed <see cref="ICheckStatusService"/> fake (TI-26 VM tests), the sibling of
/// <see cref="FakeIssueService"/>. Members a test uses are wired via settable delegates; unstubbed members
/// return benign defaults so a VM under test never has to configure operations it doesn't exercise.
/// <see cref="LastRerunId"/>/<see cref="RerunCount"/> capture re-run calls for assertions.
/// </summary>
public sealed class FakeCheckStatusService : ICheckStatusService
{
    public Func<string, bool>? IsSupportedImpl { get; set; }
    public Func<string, string, CommitChecks>? GetChecksImpl { get; set; }
    public Action<long>? RerunImpl { get; set; }

    public int GetChecksCount { get; private set; }
    public int RerunCount { get; private set; }
    public long? LastRerunId { get; private set; }

    public bool IsSupported(string repoPath) => IsSupportedImpl?.Invoke(repoPath) ?? true;

    public Task<CommitChecks> GetChecksAsync(string repoPath, string sha, CancellationToken ct)
    {
        GetChecksCount++;
        return Task.FromResult(GetChecksImpl?.Invoke(repoPath, sha) ?? CommitChecks.None(sha));
    }

    public Task RerequestAsync(string repoPath, long checkRunId, CancellationToken ct)
    {
        RerunCount++;
        LastRerunId = checkRunId;
        RerunImpl?.Invoke(checkRunId);
        return Task.CompletedTask;
    }
}
