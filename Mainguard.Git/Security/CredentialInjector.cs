using System;
using System.Collections.Generic;
using System.Text;

namespace Mainguard.Git.Security;

/// <summary>
/// Builds the env-file content injected into an agent sandbox (P2-01 ships the builder; the P2-07
/// daemon writes it to tmpfs mode 0400). Pure and in-memory — this class performs <b>no</b> file I/O,
/// so the secret never touches disk here and the builder is trivially unit-testable.
/// </summary>
public static class CredentialInjector
{
    /// <summary>
    /// Env-file content for an agent: one newline-terminated <c>KEY=value</c> line per entry, in the
    /// dictionary's enumeration order. Values are opaque tokens (no quoting/escaping), so the only
    /// validation is env-file integrity: a value containing a newline (<c>\n</c> or <c>\r</c>) would
    /// forge extra lines, so it is rejected with a typed <see cref="ArgumentException"/>.
    /// </summary>
    public static string BuildEnvFileContent(IReadOnlyDictionary<string, string> secrets)
    {
        if (secrets is null) throw new ArgumentNullException(nameof(secrets));

        var sb = new StringBuilder();
        foreach (var (name, value) in secrets)
        {
            if (value.Contains('\n') || value.Contains('\r'))
                throw new ArgumentException(
                    $"Secret '{name}' contains a newline; env-file integrity forbids it.", nameof(secrets));
            sb.Append(name).Append('=').Append(value).Append('\n');
        }
        return sb.ToString();
    }
}
