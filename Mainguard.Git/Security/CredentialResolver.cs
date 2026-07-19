using System;
using LibGit2Sharp;
using Mainguard.Git.Models;

namespace Mainguard.Git.Security;

/// <summary>
/// SSH public-key credentials for an SSH-form remote (T-14): the key pair paths and
/// the passphrase (read from the keyring, never the URL). Modeled as a Core value
/// object because the pinned LibGit2Sharp build ships <b>no</b> SSH transport / no
/// <c>SshUserKeyCredentials</c> type — SSH remote ops run through the git CLI (which
/// uses the system ssh/agent), so this is the resolved-credential seam that path
/// consumes rather than a libgit2 credentials object.
/// </summary>
public sealed class SshUserKeyCredentials
{
    public string Username { get; init; } = "git";
    public string PublicKeyPath { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    /// <summary>The key passphrase (empty when the key is unencrypted). Sourced from the keyring only.</summary>
    public string Passphrase { get; init; } = string.Empty;
}

/// <summary>
/// The credentials resolved for a remote: at most one of a libgit2 HTTP(S)
/// token credential or an SSH key credential.
/// </summary>
public readonly record struct ResolvedCredentials(Credentials? Https, SshUserKeyCredentials? Ssh)
{
    public static readonly ResolvedCredentials None = default;
    public bool HasValue => Https is not null || Ssh is not null;
}

/// <summary>
/// Builds credentials for a remote URL (T-14) — the single place the SSH-vs-token
/// decision is made.
///
/// <list type="bullet">
///   <item>SSH-form remote (<c>git@host:…</c> / <c>ssh://…</c>) → <see cref="SshUserKeyCredentials"/>
///   from the resolved key pair, with the passphrase pulled from the keyring.</item>
///   <item>HTTP(S) remote → <see cref="UsernamePasswordCredentials"/> with the host's
///   token-username convention (<c>GitHostDetector.UsernameForToken</c>) and the stored token.</item>
/// </list>
///
/// <para><b>SECURITY (G-4):</b> a secret never enters the URL — the token/passphrase
/// is only ever placed in a credentials object, never concatenated into a URL, argv, or log.</para>
/// </summary>
public static class CredentialResolver
{
    /// <summary>
    /// Resolves credentials for <paramref name="url"/>, or <see cref="ResolvedCredentials.None"/>
    /// when nothing is available (no token stored / no usable SSH key) — the caller then
    /// lets git fall back to its own credential helpers.
    /// </summary>
    public static ResolvedCredentials Resolve(string? url, ISecureKeyring keyring, SshKeyService ssh)
    {
        if (string.IsNullOrEmpty(url)) return ResolvedCredentials.None;

        if (SshKeyService.IsSshRemote(url))
        {
            var sshCred = ResolveSsh(url, ssh);
            return sshCred is null ? ResolvedCredentials.None : new ResolvedCredentials(null, sshCred);
        }

        var (host, kind) = GitHostDetector.Detect(url);
        var token = string.IsNullOrEmpty(host)
            ? null
            : keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost(host));

        // Back-compat: fall back to the legacy single "github_token" secret.
        if (string.IsNullOrEmpty(token) && kind == HostKind.GitHub)
            token = keyring.RetrieveSecret("github_token");

        if (string.IsNullOrEmpty(token)) return ResolvedCredentials.None;

        var https = new UsernamePasswordCredentials
        {
            Username = GitHostDetector.UsernameForToken(kind),
            Password = token
        };
        return new ResolvedCredentials(https, null);
    }

    private static SshUserKeyCredentials? ResolveSsh(string url, SshKeyService ssh)
    {
        var key = ResolveDefaultKey(ssh);
        if (key is null) return null;

        // The passphrase is read from the encrypted keyring — never from the URL/argv.
        var passphrase = ssh.GetPassphrase(key.PrivateKeyPath) ?? string.Empty;
        return new SshUserKeyCredentials
        {
            Username = ExtractSshUser(url) ?? "git",
            PublicKeyPath = key.PublicKeyPath,
            PrivateKeyPath = key.PrivateKeyPath,
            Passphrase = passphrase
        };
    }

    // Prefers id_ed25519, then id_rsa, then the first discovered key.
    private static SshKeyInfo? ResolveDefaultKey(SshKeyService ssh)
    {
        var keys = ssh.ListKeys();
        if (keys.Count == 0) return null;
        foreach (var preferred in new[] { "id_ed25519", "id_rsa" })
        {
            foreach (var k in keys)
                if (string.Equals(k.Name, preferred, StringComparison.OrdinalIgnoreCase))
                    return k;
        }
        return keys[0];
    }

    private static string? ExtractSshUser(string url)
    {
        var at = url.IndexOf('@');
        if (at <= 0) return null;
        var start = url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase) ? "ssh://".Length : 0;
        if (at <= start) return null;
        return url.Substring(start, at - start);
    }
}
