using System;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Models;
namespace Mainguard.App.Shell.Services;

/// <summary>
/// Registers a repository in the app's ONE repo store — <c>AppDbContext.Repositories</c>, the same
/// SQLite table the sidebar tree and the auto-detect scan (<c>ScanAutoDetectFolderAsync</c>) read and
/// write — so a repo onboarded during OOBE appears in the repo picker on first launch. Mirrors that
/// scan's persistence exactly: dedupe by path, default "Personal" category (first category as the
/// fallback), <c>LastAccessed</c> stamped. Deliberately NOT a second store (rejection trigger).
/// </summary>
public static class RepoCatalog
{
    /// <summary>True when no repository is registered in the store — the Client edition's first-run gate
    /// (1d): an empty catalog means a fresh machine (show the dedicated Clone first-run), a non-empty one
    /// means a returning client (go straight to the shell). Reads the SAME <c>AppDbContext.Repositories</c>
    /// table the sidebar tree and the reopen-last-repo card consult, so the gate can never disagree with
    /// what the shell would show.</summary>
    public static bool IsEmpty()
    {
        using var db = new AppDbContext();
        return IsEmpty(db);
    }

    /// <summary>Store-injected form of <see cref="IsEmpty()"/> — the parameterless overload reads the app
    /// DB; tests point this at a temp SQLite context so the gate is exercised deterministically.</summary>
    internal static bool IsEmpty(AppDbContext db) => !db.Repositories.Any();

    /// <summary>Adds <paramref name="path"/> to the repo list if absent. Idempotent.</summary>
    public static void EnsureRegistered(string path)
    {
        using var db = new AppDbContext();
        if (db.Repositories.Any(r => r.Path == path))
        {
            return;
        }

        var category = db.WorkspaceCategories.FirstOrDefault(c => c.Name == "Personal")
                       ?? db.WorkspaceCategories.FirstOrDefault();
        if (category is null)
        {
            return; // no category rows at all — same silent bail as the sidebar scan
        }

        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        db.Repositories.Add(new Repository
        {
            Path = path,
            DisplayName = string.IsNullOrEmpty(name) ? path : name,
            CategoryId = category.CategoryId,
            LastAccessed = DateTime.UtcNow,
        });
        db.SaveChanges();
    }
}
