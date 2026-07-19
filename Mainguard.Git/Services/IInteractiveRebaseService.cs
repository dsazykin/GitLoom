using System.Collections.Generic;
using System.Threading;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

public interface IInteractiveRebaseService
{
    IReadOnlyList<RebaseTodoItem> GetRebasePlan(string repoPath, string baseSha);
    void StartInteractiveRebase(string repoPath, string baseSha, IReadOnlyList<RebaseTodoItem> plan, CancellationToken ct = default);
    (int Step, int Total)? GetRebaseProgress(string repoPath);
}
