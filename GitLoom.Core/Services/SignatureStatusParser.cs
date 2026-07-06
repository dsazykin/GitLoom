using System;
using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure mapping of git's signature-verification output to <see cref="CommitSignatureInfo"/> (T-15).
/// No repo/IO — it only interprets the strings produced by
/// <c>git log --format=%H|%G?|%GS</c>. Kept separate from <see cref="GitService"/> so the
/// code table is unit-testable without a signing environment.
/// </summary>
public static class SignatureStatusParser
{
    /// <summary>Field separator used in the <c>--format</c> string we hand to git.</summary>
    public const char FieldSeparator = '|';

    /// <summary>The git <c>--pretty</c> format string this parser expects: SHA | %G? | signer.</summary>
    public const string LogFormat = "%H|%G?|%GS";

    /// <summary>Maps a single <c>%G?</c> code character to a <see cref="SignatureStatus"/>.</summary>
    public static SignatureStatus FromCode(char code) => code switch
    {
        'G' => SignatureStatus.Good,
        'U' => SignatureStatus.UnknownValidity,
        'B' => SignatureStatus.Bad,
        'X' => SignatureStatus.Expired,
        'Y' => SignatureStatus.ExpiredKey,
        'R' => SignatureStatus.Revoked,
        'E' => SignatureStatus.CannotCheck,
        _ => SignatureStatus.None, // 'N' and anything unexpected
    };

    /// <summary>Maps a <c>%G?</c> token (first char significant) to a <see cref="SignatureStatus"/>.</summary>
    public static SignatureStatus FromCode(string? code)
        => string.IsNullOrEmpty(code) ? SignatureStatus.None : FromCode(code[0]);

    /// <summary>
    /// Parses the multi-line output of <c>git log --no-walk --format=%H|%G?|%GS &lt;shas…&gt;</c>
    /// into a SHA → <see cref="CommitSignatureInfo"/> map. Order-independent (keyed by SHA); blank
    /// lines and malformed rows are skipped defensively so a partial read never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, CommitSignatureInfo> ParseLog(string? logOutput)
    {
        var result = new Dictionary<string, CommitSignatureInfo>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(logOutput)) return result;

        foreach (var rawLine in logOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0) continue;

            // Split into at most 3 fields; the signer (%GS) is last and may itself
            // contain the separator, so cap the split so it survives intact.
            var parts = line.Split(FieldSeparator, 3);
            var sha = parts[0].Trim();
            if (sha.Length == 0) continue;

            var status = parts.Length >= 2 ? FromCode(parts[1].Trim()) : SignatureStatus.None;
            var signer = parts.Length >= 3 ? parts[2].Trim() : string.Empty;
            result[sha] = new CommitSignatureInfo(status, signer);
        }

        return result;
    }
}
