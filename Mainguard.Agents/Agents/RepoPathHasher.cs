using System.Security.Cryptography;
using System.Text;

namespace Mainguard.Agents.Agents;

/// <summary>
/// Pure P2-06 helper: a normalized Windows repository path → a stable lowercase-hex
/// SHA-256 hash that names the bare mirror (<c>&lt;hash&gt;.git</c>) inside the VM.
///
/// <para>Normalization runs BEFORE hashing so that paths that address the same repo on a
/// case-insensitive NTFS volume collapse to one mirror: backslashes fold to a single
/// forward-slash form, any trailing separator is stripped, and the whole string is
/// lower-cased (<c>C:\Repo\</c> and <c>c:/repo</c> MUST hash identically). The hash is the
/// lowercase hex SHA-256 of the normalized UTF-8 bytes, so it is stable for Unicode paths
/// and works for Unix-style temp paths the Linux CI leg passes as the "windows repo path".</para>
/// </summary>
public static class RepoPathHasher
{
    /// <summary>Returns the lowercase-hex SHA-256 of the normalized <paramref name="windowsRepoPath"/>.</summary>
    public static string Hash(string windowsRepoPath)
    {
        var normalized = Normalize(windowsRepoPath);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Case-folds, unifies separators to <c>/</c>, and strips a single trailing separator.
    /// Kept internal so tests can pin the exact normalization contract.
    /// </summary>
    internal static string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        // Backslashes and forward slashes both mean "separator"; fold to one form.
        var unified = path.Replace('\\', '/');

        // Strip a single trailing separator so "c:/repo" and "c:/repo/" are one mirror,
        // but never strip a lone root separator down to the empty string.
        if (unified.Length > 1 && unified[^1] == '/')
        {
            unified = unified.TrimEnd('/');
            if (unified.Length == 0)
            {
                unified = "/";
            }
        }

        // NTFS is case-insensitive — fold with the invariant culture so the mapping is
        // deterministic across machine locales.
        return unified.ToLowerInvariant();
    }
}
