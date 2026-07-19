namespace GitLoom.App.Editions;

/// <summary>
/// The known editions as singletons. <see cref="App.Edition"/> defaults to <see cref="Pro"/>; the
/// <c>GITLOOM_EDITION=client</c> startup hatch (App.Initialize) or a test selects <see cref="Client"/>.
/// </summary>
public static class EditionManifests
{
    /// <summary>The shipped default: the full Pro agent platform.</summary>
    public static IEditionManifest Pro { get; } = new ProManifest();

    /// <summary>The plain Git client — no agent platform.</summary>
    public static IEditionManifest Client { get; } = new ClientManifest();
}
