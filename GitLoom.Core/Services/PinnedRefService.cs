using System;
using System.Collections.Generic;
using System.Linq;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// <see cref="IPinnedRefService"/> backed by <see cref="AppDbContext"/> (SQLite). Each operation
/// opens and disposes its own context via the injected factory — the same short-lived-handle
/// discipline the git layer uses — so nothing holds a long-lived connection. The default
/// constructor targets the app database; tests inject an in-memory context factory.
/// </summary>
public sealed class PinnedRefService : IPinnedRefService
{
    private readonly Func<AppDbContext> _contextFactory;

    public PinnedRefService() : this(() => new AppDbContext()) { }

    public PinnedRefService(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory;
    }

    public IReadOnlyList<PinnedRef> GetPinnedRefs(string repoPath)
    {
        using var ctx = _contextFactory();
        return ctx.PinnedRefs
            .Where(p => p.RepoPath == repoPath)
            .OrderBy(p => p.Order)
            .ToList();
    }

    public void Pin(string repoPath, string refName)
    {
        using var ctx = _contextFactory();
        bool exists = ctx.PinnedRefs.Any(p => p.RepoPath == repoPath && p.RefName == refName);
        if (exists) return;

        int nextOrder = ctx.PinnedRefs.Where(p => p.RepoPath == repoPath)
            .Select(p => (int?)p.Order)
            .Max() ?? -1;
        nextOrder += 1;

        ctx.PinnedRefs.Add(new PinnedRef { RepoPath = repoPath, RefName = refName, Order = nextOrder });
        ctx.SaveChanges();
    }

    public void Unpin(string repoPath, string refName)
    {
        using var ctx = _contextFactory();
        var existing = ctx.PinnedRefs.FirstOrDefault(p => p.RepoPath == repoPath && p.RefName == refName);
        if (existing == null) return;

        ctx.PinnedRefs.Remove(existing);
        ctx.SaveChanges();
    }

    public bool IsPinned(string repoPath, string refName)
    {
        using var ctx = _contextFactory();
        return ctx.PinnedRefs.Any(p => p.RepoPath == repoPath && p.RefName == refName);
    }
}
