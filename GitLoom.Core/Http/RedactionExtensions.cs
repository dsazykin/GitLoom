namespace GitLoom.Core.Http;

/// <summary>
/// The single sanctioned secret-scrub helper (G-4). Removes a known secret substring (an API key,
/// an OAuth/PAT token) from arbitrary host/error text before it can reach an exception message, a
/// log line, or the UI. Centralised here so there is exactly <b>one</b> copy of the token-scrub
/// logic — a second copy is a review rejection trigger. <see cref="GitLoom.Core.Hosting"/>'s
/// GitHubApiClient and the P2-01 <see cref="GitLoom.Core.Security.ApiKeyHealthService"/> both
/// delegate here rather than re-implementing it.
/// </summary>
internal static class RedactionExtensions
{
    /// <summary>
    /// Returns <paramref name="text"/> with every occurrence of <paramref name="secret"/> replaced by
    /// <c>***</c>. A null/empty text or secret is returned unchanged (there is nothing to scrub).
    /// </summary>
    public static string Redact(string text, string secret)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(secret)) return text;
        return text.Replace(secret, "***");
    }
}
