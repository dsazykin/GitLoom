using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;

namespace GitLoom.Tests.Fakes;

/// <summary>
/// Delegate-backed <see cref="IIssueService"/> fake (TI-24 VM tests), the sibling of
/// <see cref="FakePullRequestService"/>. Members a test uses are wired via settable delegates; unstubbed
/// members return benign defaults so a VM under test never has to configure operations it doesn't
/// exercise. <see cref="LastFilter"/> captures the last list filter so filter-reload can be asserted.
/// </summary>
public sealed class FakeIssueService : IIssueService
{
    public Func<string, bool>? IsSupportedImpl { get; set; }
    public Func<string, IssueState, IReadOnlyList<IssueItem>>? ListImpl { get; set; }
    public Func<string, CreateIssue, IssueItem>? CreateImpl { get; set; }
    public Func<string, int, string, IssueComment>? CommentImpl { get; set; }
    public Func<string, int, IssueState, IssueItem>? SetStateImpl { get; set; }

    public IssueState? LastFilter { get; private set; }
    public int CreateCount { get; private set; }
    public (int Number, IssueState State)? LastSetState { get; private set; }
    public (int Number, string Body)? LastComment { get; private set; }

    public bool IsSupported(string repoPath) => IsSupportedImpl?.Invoke(repoPath) ?? true;

    public Task<IReadOnlyList<IssueItem>> ListAsync(string repoPath, IssueState filter, CancellationToken ct)
    {
        LastFilter = filter;
        return Task.FromResult(ListImpl?.Invoke(repoPath, filter) ?? Array.Empty<IssueItem>());
    }

    public Task<IssueDetail> GetAsync(string repoPath, int number, CancellationToken ct)
        => Task.FromResult(new IssueDetail());

    public Task<IssueItem> CreateAsync(string repoPath, CreateIssue request, CancellationToken ct)
    {
        CreateCount++;
        return Task.FromResult(CreateImpl?.Invoke(repoPath, request) ?? new IssueItem { Number = 1, Title = request.Title });
    }

    public Task<IssueComment> CommentAsync(string repoPath, int number, string body, CancellationToken ct)
    {
        LastComment = (number, body);
        return Task.FromResult(CommentImpl?.Invoke(repoPath, number, body) ?? new IssueComment { Body = body });
    }

    public Task<IssueItem> SetStateAsync(string repoPath, int number, IssueState state, CancellationToken ct)
    {
        LastSetState = (number, state);
        return Task.FromResult(SetStateImpl?.Invoke(repoPath, number, state) ?? new IssueItem { Number = number, State = state });
    }
}
