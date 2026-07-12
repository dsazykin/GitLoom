using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Agents.Sandbox;

/// <summary>The manifest/lockfile contents a project declares, fed to <see cref="DeclaredDependencyResolver"/>.</summary>
public sealed record DeclaredDependencyInputs(
    string? GoMod = null,
    string? PackageJson = null,
    string? PackageLockJson = null);

/// <summary>
/// The exact module set the package proxy may serve for a repo (F5). Membership is the deny-boundary:
/// a fetch of a module outside this set is refused (typed) — closing the "pull-only ≠ cannot fetch
/// attacker code" gap by scoping the language proxy to declared dependencies.
/// </summary>
public sealed class DeclaredDependencySet
{
    private readonly HashSet<string> _modules;

    public DeclaredDependencySet(IEnumerable<string> modules) =>
        _modules = new HashSet<string>(modules, StringComparer.OrdinalIgnoreCase);

    /// <summary>The declared modules (normalised, deduplicated).</summary>
    public IReadOnlySet<string> Modules => _modules;

    /// <summary>Is <paramref name="module"/> a declared dependency (exact, or a subpath of one)?</summary>
    public bool Allows(string module)
    {
        if (string.IsNullOrWhiteSpace(module)) return false;
        var m = module.Trim();
        if (_modules.Contains(m)) return true;

        // A Go import path may be a subpath of a declared module (e.g. the module is
        // github.com/a/b and the request is github.com/a/b/sub). Scope by module-path prefix.
        return _modules.Any(declared =>
            m.StartsWith(declared + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Throws <see cref="DeclaredDependencyDeniedException"/> if <paramref name="module"/> is out of scope.</summary>
    public void EnsureAllowed(string module)
    {
        if (!Allows(module))
            throw new DeclaredDependencyDeniedException(
                $"Module '{module}' is not a declared dependency; the package proxy will not fetch it (F5 declared-dependency scope).");
    }
}

/// <summary>
/// Pure F5 resolver: parses <c>go.mod</c> / <c>package.json</c> / <c>package-lock.json</c> into the
/// exact module set the package proxy may serve. No I/O — the caller reads the files and passes their
/// contents. Requests outside the resolved set are denied by <see cref="DeclaredDependencySet.EnsureAllowed"/>.
/// </summary>
public static class DeclaredDependencyResolver
{
    public static DeclaredDependencySet Resolve(DeclaredDependencyInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var modules = new List<string>();

        if (!string.IsNullOrWhiteSpace(inputs.GoMod))
            modules.AddRange(ParseGoMod(inputs.GoMod));
        if (!string.IsNullOrWhiteSpace(inputs.PackageJson))
            modules.AddRange(ParsePackageJsonDependencies(inputs.PackageJson));
        if (!string.IsNullOrWhiteSpace(inputs.PackageLockJson))
            modules.AddRange(ParsePackageLock(inputs.PackageLockJson));

        return new DeclaredDependencySet(modules);
    }

    /// <summary>Extracts the module paths from a <c>require</c> block (single-line or grouped).</summary>
    private static IEnumerable<string> ParseGoMod(string goMod)
    {
        var inBlock = false;
        foreach (var raw in goMod.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            if (inBlock)
            {
                if (line.StartsWith(")", StringComparison.Ordinal)) { inBlock = false; continue; }
                var mod = FirstToken(line);
                if (mod is not null) yield return mod;
                continue;
            }

            if (line.StartsWith("require ", StringComparison.Ordinal))
            {
                var rest = line["require ".Length..].Trim();
                if (rest.StartsWith("(", StringComparison.Ordinal)) { inBlock = true; continue; }
                var mod = FirstToken(rest);
                if (mod is not null) yield return mod;
            }
        }
    }

    private static IEnumerable<string> ParsePackageJsonDependencies(string packageJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(packageJson); }
        catch (JsonException) { yield break; }

        using (doc)
        {
            foreach (var section in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(section, out var deps)
                    && deps.ValueKind == JsonValueKind.Object)
                {
                    foreach (var dep in deps.EnumerateObject())
                        yield return dep.Name;
                }
            }
        }
    }

    private static IEnumerable<string> ParsePackageLock(string lockJson)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(lockJson); }
        catch (JsonException) { yield break; }

        using (doc)
        {
            // lockfile v2/v3: "packages": { "node_modules/foo": {...} }
            if (doc.RootElement.TryGetProperty("packages", out var packages) && packages.ValueKind == JsonValueKind.Object)
            {
                foreach (var pkg in packages.EnumerateObject())
                {
                    var key = pkg.Name;
                    var idx = key.LastIndexOf("node_modules/", StringComparison.Ordinal);
                    if (idx >= 0)
                        yield return key[(idx + "node_modules/".Length)..];
                }
            }

            // lockfile v1: "dependencies": { "foo": {...} }
            if (doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
            {
                foreach (var dep in deps.EnumerateObject())
                    yield return dep.Name;
            }
        }
    }

    private static string StripComment(string line)
    {
        var idx = line.IndexOf("//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static string? FirstToken(string line)
    {
        var token = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }
}
