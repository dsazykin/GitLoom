using GitLoom.Core.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// F5 declared-dependency scoping: the package proxy may only serve modules the project declared, so
/// an arbitrary VCS / second-stage-payload fetch is denied (typed) rather than silently proxied.
/// </summary>
public class DeclaredDependencyResolverTests
{
    private const string GoMod = """
        module example.com/app

        go 1.22

        require (
            github.com/spf13/cobra v1.8.0
            golang.org/x/sync v0.6.0 // indirect
        )

        require github.com/stretchr/testify v1.9.0
        """;

    private const string PackageJson = """
        {
          "name": "app",
          "dependencies": { "react": "^18.0.0", "@scope/pkg": "1.2.3" },
          "devDependencies": { "typescript": "^5.0.0" }
        }
        """;

    [Fact]
    public void Resolve_GoMod_ExtractsDeclaredModules()
    {
        var set = DeclaredDependencyResolver.Resolve(new DeclaredDependencyInputs(GoMod: GoMod));

        Assert.Contains("github.com/spf13/cobra", set.Modules);
        Assert.Contains("golang.org/x/sync", set.Modules);
        Assert.Contains("github.com/stretchr/testify", set.Modules);
        Assert.DoesNotContain("example.com/app", set.Modules); // the module declaration itself is not a dependency
    }

    [Fact]
    public void Resolve_PackageJson_ExtractsDepsAndDevDeps()
    {
        var set = DeclaredDependencyResolver.Resolve(new DeclaredDependencyInputs(PackageJson: PackageJson));

        Assert.Contains("react", set.Modules);
        Assert.Contains("@scope/pkg", set.Modules);
        Assert.Contains("typescript", set.Modules);
    }

    [Fact]
    public void Allows_SubpathOfDeclaredGoModule()
    {
        var set = DeclaredDependencyResolver.Resolve(new DeclaredDependencyInputs(GoMod: GoMod));
        Assert.True(set.Allows("github.com/spf13/cobra/doc"));
    }

    [Fact]
    public void EnsureAllowed_OutOfSetModule_ThrowsTyped()
    {
        var set = DeclaredDependencyResolver.Resolve(new DeclaredDependencyInputs(GoMod: GoMod));

        Assert.False(set.Allows("github.com/attacker/payload"));
        Assert.Throws<DeclaredDependencyDeniedException>(() => set.EnsureAllowed("github.com/attacker/payload"));
    }

    [Fact]
    public void Resolve_PackageLock_ExtractsModules()
    {
        const string lockJson = """
            {
              "lockfileVersion": 3,
              "packages": {
                "": { "name": "app" },
                "node_modules/left-pad": { "version": "1.3.0" },
                "node_modules/@scope/pkg": { "version": "1.2.3" }
              }
            }
            """;
        var set = DeclaredDependencyResolver.Resolve(new DeclaredDependencyInputs(PackageLockJson: lockJson));

        Assert.Contains("left-pad", set.Modules);
        Assert.Contains("@scope/pkg", set.Modules);
    }
}
