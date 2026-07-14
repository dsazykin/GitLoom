using System;
using System.Collections.Generic;
using System.Linq;

namespace GitLoom.Core.Security;

/// <summary>A parsed, non-secret <c>gitloom://</c> deep link. Only navigation intents exist — never
/// anything that carries a credential.</summary>
public abstract record DeepLinkCommand
{
    /// <summary>Open a registered repository by its opaque path-hash id (T-... repo hash, never a path).</summary>
    public sealed record OpenRepo(string RepoId) : DeepLinkCommand;

    /// <summary>Open a pull request view: host + <c>owner/repo</c> slug + PR number.</summary>
    public sealed record OpenPr(string Host, string Repo, int Number) : DeepLinkCommand;

    /// <summary>Open an agent's cockpit by agent id.</summary>
    public sealed record OpenAgent(string AgentId) : DeepLinkCommand;
}

/// <summary>The three outcomes of parsing an incoming deep link.</summary>
public enum DeepLinkOutcome
{
    /// <summary>A well-formed, non-secret command to dispatch.</summary>
    Command,
    /// <summary>A syntactically valid <c>gitloom://</c> link with an unknown verb — ignored gracefully.</summary>
    Ignored,
    /// <summary>Rejected: not a <c>gitloom://</c> link, malformed, or carrying a secret-shaped parameter.</summary>
    Rejected,
}

/// <summary>The result of <see cref="DeepLinkParser.Parse"/>.</summary>
public sealed record DeepLinkResult(DeepLinkOutcome Outcome, DeepLinkCommand? Command, string? Reason)
{
    public static DeepLinkResult Ok(DeepLinkCommand command) => new(DeepLinkOutcome.Command, command, null);
    public static DeepLinkResult Ignore(string reason) => new(DeepLinkOutcome.Ignored, null, reason);
    public static DeepLinkResult Reject(string reason) => new(DeepLinkOutcome.Rejected, null, reason);
}

/// <summary>
/// Parses and builds <c>gitloom://</c> deep links. The <b>hard invariant</b> (P2-22 invariant 1): a
/// <c>gitloom://</c> URL never carries a secret. Parsing rejects any link whose query/fragment keys
/// match a secret pattern; the builder API accepts no token-typed inputs at all, so a secret cannot be
/// placed in a link even by mistake (the code-path guarantee the reviewer grep backs up).
/// </summary>
public static class DeepLinkParser
{
    public const string Scheme = "gitloom";

    /// <summary>Query/fragment key patterns that must never appear in a <c>gitloom://</c> link.</summary>
    private static readonly string[] SecretKeyPatterns =
    {
        "token", "code", "secret", "key", "password", "passwd", "pwd", "credential", "auth", "bearer", "jwt", "assertion",
    };

    /// <summary>True if <paramref name="key"/> looks like a secret-bearing parameter name.</summary>
    public static bool IsSecretKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var lower = key.ToLowerInvariant();
        return SecretKeyPatterns.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }

    public static DeepLinkResult Parse(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri) || !Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            return DeepLinkResult.Reject("Not an absolute URI.");

        if (!string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            return DeepLinkResult.Reject($"Scheme '{parsed.Scheme}' is not '{Scheme}'.");

        // Secret guard runs before any dispatch: inspect every query AND fragment key.
        foreach (var key in KeysOf(parsed.Query).Concat(KeysOf(TrimHash(parsed.Fragment))))
        {
            if (IsSecretKey(key))
                return DeepLinkResult.Reject($"Link carries a secret-shaped parameter '{key}'; refused.");
        }

        var verb = parsed.Host.ToLowerInvariant();
        var segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToArray();

        switch (verb)
        {
            case "open-repo":
            case "repo":
                return segments.Length == 1
                    ? DeepLinkResult.Ok(new DeepLinkCommand.OpenRepo(segments[0]))
                    : DeepLinkResult.Reject("open-repo expects exactly one path segment (the repo id).");

            case "open-pr":
            case "pr":
                // gitloom://open-pr/<host>/<owner>/<repo>/<number>  (owner+repo may be multi-segment).
                if (segments.Length < 3 || !int.TryParse(segments[^1], out var number) || number <= 0)
                    return DeepLinkResult.Reject("open-pr expects <host>/<owner/repo>/<number>.");
                var host = segments[0];
                var repo = string.Join('/', segments[1..^1]);
                return DeepLinkResult.Ok(new DeepLinkCommand.OpenPr(host, repo, number));

            case "open-agent":
            case "agent":
                return segments.Length == 1
                    ? DeepLinkResult.Ok(new DeepLinkCommand.OpenAgent(segments[0]))
                    : DeepLinkResult.Reject("open-agent expects exactly one path segment (the agent id).");

            default:
                return DeepLinkResult.Ignore($"Unknown verb '{verb}'.");
        }
    }

    private static string TrimHash(string fragment) => fragment.StartsWith('#') ? fragment[1..] : fragment;

    private static IEnumerable<string> KeysOf(string query)
    {
        var trimmed = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            yield return Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
        }
    }
}

/// <summary>
/// Builds <c>gitloom://</c> links. Every method takes only navigation identifiers — there is no
/// overload anywhere that accepts a token/secret, which is the code-path half of invariant 1 (the
/// other half is the parse-time secret guard). A defensive check refuses an identifier that itself
/// looks like a secret parameter.
/// </summary>
public static class DeepLinkBuilder
{
    public static string OpenRepo(string repoId) =>
        $"{DeepLinkParser.Scheme}://open-repo/{SafeSegment(repoId)}";

    public static string OpenPr(string host, string repo, int number)
    {
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number), "PR number must be positive.");
        var repoPath = string.Join('/', repo.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeSegment));
        return $"{DeepLinkParser.Scheme}://open-pr/{SafeSegment(host)}/{repoPath}/{number}";
    }

    public static string OpenAgent(string agentId) =>
        $"{DeepLinkParser.Scheme}://open-agent/{SafeSegment(agentId)}";

    private static string SafeSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (DeepLinkParser.IsSecretKey(value))
            throw new ArgumentException($"Refusing to place a secret-shaped value '{value}' in a deep link.", nameof(value));
        return Uri.EscapeDataString(value);
    }
}
