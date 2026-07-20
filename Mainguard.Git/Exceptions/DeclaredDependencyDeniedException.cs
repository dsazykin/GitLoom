namespace Mainguard.Git.Exceptions;

/// <summary>
/// A package/module fetch was denied because the requested module is not in the set the
/// project declared (F5): the package proxy may only serve modules resolved from
/// <c>go.mod</c>/<c>package.json</c>/lockfiles, so an arbitrary VCS/second-stage-payload
/// fetch is refused (typed) rather than silently proxied.
/// </summary>
public sealed class DeclaredDependencyDeniedException : MainguardException
{
    public DeclaredDependencyDeniedException(string message) : base(message) { }
}
