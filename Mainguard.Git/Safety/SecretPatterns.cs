using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mainguard.Git.Safety;

/// <summary>
/// One named secret-detection rule. Pure: it decides whether a line contains a secret but NEVER
/// emits, returns, or stores the matched value — callers build a message from <see cref="DisplayName"/>
/// and the location only. This is the CRITICAL no-leak seam of T-30.
/// </summary>
public sealed class SecretPattern
{
    /// <summary>Stable rule id, e.g. "aws-access-key-id".</summary>
    public required string Rule { get; init; }

    /// <summary>Human-facing rule name for the finding message, e.g. "AWS access key id".</summary>
    public required string DisplayName { get; init; }

    /// <summary>The detection regex. A named group <c>v</c>, when present, is the candidate value passed to <see cref="Validator"/>.</summary>
    public required Regex Pattern { get; init; }

    /// <summary>
    /// Optional secondary guard applied to the captured value (group <c>v</c>, else the whole match)
    /// to suppress obvious false positives on the generic assignment rule. Never surfaces the value.
    /// </summary>
    internal Func<string, bool>? Validator { get; init; }

    /// <summary>
    /// True iff <paramref name="line"/> contains a (validated) secret. Returns only a boolean — the
    /// matched text is never returned so a caller cannot accidentally echo it.
    /// </summary>
    public bool IsMatch(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        foreach (Match m in Pattern.Matches(line))
        {
            if (Validator is null) return true;
            var value = m.Groups["v"].Success ? m.Groups["v"].Value : m.Value;
            if (Validator(value)) return true;
        }
        return false;
    }
}

/// <summary>
/// Pure catalog of the T-30 secret rules (no IO, no Avalonia). Each rule matches a planted sample
/// without the caller ever seeing the value. Used by <see cref="PreCommitScanEngine"/>.
/// </summary>
public static class SecretPatterns
{
    private const RegexOptions Opts = RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Placeholder / dummy values we refuse to flag on the generic assignment rule so ordinary config
    // samples ("password = \"changeme\"") don't cry wolf. Case-insensitive substring match.
    private static readonly string[] Placeholders =
    {
        "example", "placeholder", "changeme", "your_", "yourkey", "yourtoken", "xxxxxxxx",
        "redacted", "dummy", "sample", "notreal", "test1234", "password123", "<", ">", "{{", "}}"
    };

    /// <summary>All secret rules, in a stable order.</summary>
    public static IReadOnlyList<SecretPattern> All { get; } = new List<SecretPattern>
    {
        new()
        {
            Rule = "aws-access-key-id",
            DisplayName = "AWS access key id",
            Pattern = new Regex(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA|ANPA)[0-9A-Z]{16}\b", Opts),
        },
        new()
        {
            Rule = "aws-secret-access-key",
            DisplayName = "AWS secret access key",
            // Requires the well-known key name as context so a bare 40-char base64 string doesn't
            // false-positive; the value itself (group v) is never surfaced.
            Pattern = new Regex(
                @"(?i)aws.{0,20}?(?:secret|private).{0,20}?(?:key|token)['""]?\s*[:=]\s*['""]?(?<v>[A-Za-z0-9/+]{40})",
                Opts),
        },
        new()
        {
            Rule = "github-token",
            DisplayName = "GitHub token",
            Pattern = new Regex(@"\b(?:gh[psuor]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b", Opts),
        },
        new()
        {
            Rule = "google-api-key",
            DisplayName = "Google API key",
            Pattern = new Regex(@"\bAIza[0-9A-Za-z\-_]{35}\b", Opts),
        },
        new()
        {
            Rule = "slack-token",
            DisplayName = "Slack token",
            Pattern = new Regex(@"\bxox[baprs]-[0-9A-Za-z-]{10,}\b", Opts),
        },
        new()
        {
            Rule = "private-key-block",
            DisplayName = "private key block",
            Pattern = new Regex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH |PGP |ENCRYPTED )?PRIVATE KEY-----", Opts),
        },
        new()
        {
            Rule = "jwt",
            DisplayName = "JSON Web Token",
            Pattern = new Regex(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b", Opts),
        },
        new()
        {
            Rule = "generic-secret-assignment",
            DisplayName = "hard-coded secret",
            // secret/api_key/password = "…" — high-entropy assignment. The value (group v) is
            // validated for entropy and against a placeholder list, and is never emitted.
            Pattern = new Regex(
                @"(?i)\b(?:api[_-]?key|apikey|secret|secret[_-]?key|access[_-]?token|auth[_-]?token|password|passwd|client[_-]?secret)\b\s*[:=]\s*['""](?<v>[^'""\s]{12,})['""]",
                Opts),
            Validator = IsLikelySecretValue,
        },
    };

    /// <summary>
    /// True iff the assigned value looks like a real secret rather than a placeholder: not a known
    /// placeholder token and carrying enough Shannon entropy to be a random credential.
    /// </summary>
    private static bool IsLikelySecretValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 12) return false;

        foreach (var ph in Placeholders)
        {
            if (value.Contains(ph, StringComparison.OrdinalIgnoreCase)) return false;
        }

        // A single repeated character (e.g. "xxxxxxxxxxxx") is not a secret.
        return ShannonEntropy(value) >= 3.0;
    }

    /// <summary>Shannon entropy in bits per character of <paramref name="s"/>.</summary>
    internal static double ShannonEntropy(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in s)
        {
            counts.TryGetValue(c, out var n);
            counts[c] = n + 1;
        }

        double entropy = 0;
        double len = s.Length;
        foreach (var n in counts.Values)
        {
            double p = n / len;
            entropy -= p * Math.Log2(p);
        }
        return entropy;
    }
}
