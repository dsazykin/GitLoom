using System;
using System.Collections.Generic;
using System.Linq;
using GitLoom.Core.Models;

namespace GitLoom.Core.Review;

/// <summary>
/// Review-risk taxonomy (P2-11 contract §2). Lower <see cref="HunkRisk.Rank"/> = review first. The rank
/// is the enum's declaration order (ExecutableConfig = 0 … Docs = 7) so the cockpit's hunk list is the
/// review plan.
/// </summary>
public enum RiskCategory
{
    ExecutableConfig,
    Lockfile,
    CiWorkflow,
    GitHooks,
    EditorConfig,
    SecuritySensitivePath,
    Source,
    Docs,
}

/// <summary>One hunk's classification: its <see cref="RiskCategory"/> plus the review-order rank.</summary>
public sealed record HunkRisk(RiskCategory Category, int Rank);

/// <summary>
/// Pure path + content classifier (P2-11 step 1). No repo, no IO. The UI renders this; it never
/// re-derives it (invariant 1). The content rule for <c>package.json</c> is load-bearing: a hunk that
/// touches the <c>"scripts"</c> block is <see cref="RiskCategory.ExecutableConfig"/> (arbitrary code at
/// install/build time); a dependency-version-only hunk is <see cref="RiskCategory.Lockfile"/> (edge rows
/// 1–2). Renamed files classify by their <b>new</b> path + content (edge row 4).
/// </summary>
public static class RiskClassifier
{
    /// <summary>The review-order rank for a category (== its enum ordinal; ExecutableConfig = 0 … Docs = 7).</summary>
    public static int RankOf(RiskCategory category) => (int)category;

    /// <summary>Classifies one hunk of a file by path rules + (for <c>package.json</c>) content rules.</summary>
    public static HunkRisk Classify(string filePath, DiffHunk hunk)
    {
        var category = ClassifyCategory(filePath ?? string.Empty, hunk);
        return new HunkRisk(category, RankOf(category));
    }

    private static RiskCategory ClassifyCategory(string filePath, DiffHunk hunk)
    {
        var path = Normalize(filePath);
        var name = FileName(path);

        // Content rule FIRST: package.json is dangerous only where it edits the scripts block.
        if (name == "package.json")
        {
            return TouchesScriptsBlock(hunk) ? RiskCategory.ExecutableConfig : RiskCategory.Lockfile;
        }

        // Lockfiles (dependency manifests): by well-known name or the *.lock extension.
        if (IsLockfileName(name) || name.EndsWith(".lock", StringComparison.Ordinal))
        {
            return RiskCategory.Lockfile;
        }

        if (path.Contains(".github/workflows/", StringComparison.Ordinal))
        {
            return RiskCategory.CiWorkflow;
        }

        // Git hooks: husky config and any hook-installing path.
        if (path.Contains(".husky", StringComparison.Ordinal)
            || path.Contains("git-hooks", StringComparison.Ordinal)
            || path.Contains(".git/hooks", StringComparison.Ordinal)
            || name is ".huskyrc" or ".huskyrc.json" or ".pre-commit-config.yaml")
        {
            return RiskCategory.GitHooks;
        }

        if (path.Contains(".vscode/", StringComparison.Ordinal) || name == ".editorconfig")
        {
            return RiskCategory.EditorConfig;
        }

        // Security-sensitive heuristics (path segments + name substrings).
        if (HasSegment(path, "auth") || HasSegment(path, "crypto")
            || path.Contains("security", StringComparison.Ordinal)
            || path.Contains("credential", StringComparison.Ordinal))
        {
            return RiskCategory.SecuritySensitivePath;
        }

        if (IsDocsPath(path, name))
        {
            return RiskCategory.Docs;
        }

        return RiskCategory.Source;
    }

    // A package.json hunk edits the scripts block if any ADD/DELETE line sits inside `"scripts": { … }`.
    // We can only see the hunk, so we track brace depth across its (context + changed) lines, seeding the
    // "inside scripts" state from the section heading (git often emits `@@ … @@ "scripts": {`).
    private static bool TouchesScriptsBlock(DiffHunk hunk)
    {
        if (hunk is null)
        {
            return false;
        }

        var insideScripts = hunk.SectionHeading?.Contains("\"scripts\"", StringComparison.Ordinal) == true;
        var depth = insideScripts ? 1 : 0;

        foreach (var line in hunk.Lines)
        {
            var text = line.Text ?? string.Empty;
            var opensScriptsHere = !insideScripts && MentionsScriptsKey(text);

            if (opensScriptsHere)
            {
                insideScripts = true;
                depth = BraceDelta(text);
                // A changed `"scripts":` declaration line itself counts as touching the block.
                if (line.Kind != DiffLineKind.Context)
                {
                    return true;
                }

                if (depth <= 0)
                {
                    insideScripts = false;
                }

                continue;
            }

            if (insideScripts)
            {
                if (line.Kind != DiffLineKind.Context)
                {
                    return true;
                }

                depth += BraceDelta(text);
                if (depth <= 0)
                {
                    insideScripts = false;
                }
            }
        }

        return false;
    }

    private static bool MentionsScriptsKey(string text) =>
        text.Contains("\"scripts\"", StringComparison.Ordinal);

    private static int BraceDelta(string text)
    {
        var delta = 0;
        foreach (var c in text)
        {
            if (c == '{')
            {
                delta++;
            }
            else if (c == '}')
            {
                delta--;
            }
        }

        return delta;
    }

    private static bool IsLockfileName(string name) => name is
        "package-lock.json" or "npm-shrinkwrap.json" or "pnpm-lock.yaml" or "yarn.lock"
        or "poetry.lock" or "cargo.lock" or "gemfile.lock" or "composer.lock" or "packages.lock.json";

    private static bool IsDocsPath(string path, string name)
    {
        var ext = Extension(name);
        if (ext is ".md" or ".mdx" or ".markdown" or ".rst" or ".adoc" or ".txt")
        {
            return true;
        }

        return HasSegment(path, "docs") || name is "readme" or "license" or "changelog";
    }

    private static bool HasSegment(string path, string segment) =>
        path == segment
        || path.StartsWith(segment + "/", StringComparison.Ordinal)
        || path.Contains("/" + segment + "/", StringComparison.Ordinal)
        || path.EndsWith("/" + segment, StringComparison.Ordinal);

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/').ToLowerInvariant();

    private static string FileName(string normalizedPath)
    {
        var slash = normalizedPath.LastIndexOf('/');
        return slash < 0 ? normalizedPath : normalizedPath[(slash + 1)..];
    }

    private static string Extension(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot < 0 ? string.Empty : name[dot..];
    }
}

/// <summary>
/// Pure helper: the file path a <see cref="FilePatch"/> concerns, resolved to its <b>new</b> path (edge
/// row 4 — a rename classifies by its destination). Reads the patch header only; no repo/IO.
/// </summary>
public static class FilePatchPath
{
    /// <summary>The new-side path of a file patch (rename-aware, deletion falls back to the old path).</summary>
    public static string NewPath(FilePatch patch)
    {
        if (patch is null)
        {
            return string.Empty;
        }

        var lines = (patch.Header ?? string.Empty).Split('\n');

        // Prefer an explicit `rename to <path>`.
        foreach (var line in lines)
        {
            if (line.StartsWith("rename to ", StringComparison.Ordinal))
            {
                return line["rename to ".Length..].Trim();
            }
        }

        // Then the `+++ b/<path>` line (skip a deletion's `+++ /dev/null`).
        foreach (var line in lines)
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var value = line["+++ ".Length..].Trim();
                if (value is not ("/dev/null" or ""))
                {
                    return StripPrefix(value);
                }
            }
        }

        // Fall back to the `diff --git a/x b/y` destination, then the `--- a/<path>` (a deletion).
        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                var parts = line["diff --git ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    return StripPrefix(parts[1].Trim());
                }
            }
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                var value = line["--- ".Length..].Trim();
                if (value is not ("/dev/null" or ""))
                {
                    return StripPrefix(value);
                }
            }
        }

        return string.Empty;
    }

    private static string StripPrefix(string value)
    {
        if (value.StartsWith("a/", StringComparison.Ordinal) || value.StartsWith("b/", StringComparison.Ordinal))
        {
            return value[2..];
        }

        return value;
    }
}
