using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Tests.Fakes;

/// <summary>
/// Delegate-backed <see cref="IReleaseService"/> fake (TI-28 VM/render tests), the sibling of
/// <see cref="FakeIssueService"/>. Members a test uses are wired via settable delegates; unstubbed members
/// return benign defaults so a VM under test never has to configure operations it doesn't exercise.
/// <see cref="LastCreate"/> captures the last create request so publish can be asserted.
/// </summary>
public sealed class FakeReleaseService : IReleaseService
{
    public Func<string, bool>? IsSupportedImpl { get; set; }
    public Func<string, IReadOnlyList<ReleaseItem>>? ListImpl { get; set; }
    public Func<string, string, string, string>? GenerateNotesImpl { get; set; }
    public Func<string, CreateRelease, ReleaseItem>? CreateImpl { get; set; }

    public int CreateCount { get; private set; }
    public CreateRelease? LastCreate { get; private set; }
    public (string NewTag, string Target)? LastGenerate { get; private set; }

    public bool IsSupported(string repoPath) => IsSupportedImpl?.Invoke(repoPath) ?? true;

    public Task<IReadOnlyList<ReleaseItem>> ListAsync(string repoPath, CancellationToken ct)
        => Task.FromResult(ListImpl?.Invoke(repoPath) ?? Array.Empty<ReleaseItem>());

    public string GenerateNotes(string repoPath, string newTag, string targetCommitish)
    {
        LastGenerate = (newTag, targetCommitish);
        return GenerateNotesImpl?.Invoke(repoPath, newTag, targetCommitish) ?? "";
    }

    public Task<ReleaseItem> CreateAsync(string repoPath, CreateRelease request, CancellationToken ct)
    {
        CreateCount++;
        LastCreate = request;
        return Task.FromResult(CreateImpl?.Invoke(repoPath, request)
            ?? new ReleaseItem { TagName = request.TagName, Name = request.Name, IsDraft = request.IsDraft, IsPrerelease = request.IsPrerelease });
    }
}
