using System.Collections.Generic;
using System.Threading;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

public interface IInteractiveRebaseService
{
    IReadOnlyList<RebaseTodoItem> GetRebasePlan(string repoPath, string baseSha);
    void StartInteractiveRebase(string repoPath, string baseSha, IReadOnlyList<RebaseTodoItem> plan, CancellationToken ct = default);
    (int Step, int Total)? GetRebaseProgress(string repoPath);
}
