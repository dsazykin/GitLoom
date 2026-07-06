using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GitLoom.Core.Security;

/// <summary>One SSH key pair discovered in (or generated into) the user's <c>~/.ssh</c>.</summary>
public sealed class SshKeyInfo
{
    /// <summary>Base file name of the private key (e.g. <c>id_ed25519</c>).</summary>
    public string Name { get; init; } = string.Empty;
    public string PrivateKeyPath { get; init; } = string.Empty;
    public string PublicKeyPath { get; init; } = string.Empty;
    /// <summary>Full text of the <c>.pub</c> file (safe to display / copy — it is public).</summary>
    public string PublicKeyText { get; init; } = string.Empty;
    /// <summary>Key type parsed from the public key (e.g. <c>ssh-ed25519</c>).</summary>
    public string KeyType { get; init; } = string.Empty;
    /// <summary>Trailing comment on the public key, if any.</summary>
    public string Comment { get; init; } = string.Empty;
    /// <summary><c>true</c> when a passphrase for this key is stored in the keyring.</summary>
    public bool HasStoredPassphrase { get; init; }
}

/// <summary>
/// SSH key manager (T-14): generates ed25519 keys, lists <c>~/.ssh</c> keys, exposes
/// the public key for copying, and stores per-key passphrases in the keyring.
///
/// <para><b>SECURITY (G-4).</b> Key generation shells out to <c>ssh-keygen</c> via
/// <see cref="ProcessStartInfo.ArgumentList"/> — <b>never</b> a shell string, so a
/// passphrase can never be word-split, glob-expanded, or leak through shell history.
/// The passphrase is one <c>ArgumentList</c> element (<c>-N &lt;passphrase&gt;</c>);
/// ssh-keygen has no non-interactive stdin route for a <i>new</i> passphrase, and it
/// is a purely <b>local</b> key-gen tool, so this argv element never reaches any
/// network path, URL, or log. The passphrase is then persisted only in the
/// <see cref="SecureKeyring"/> (encrypted), keyed <c>sshpass_&lt;sanitized-keypath&gt;</c>.</para>
/// </summary>
public sealed class SshKeyService
{
    private readonly ISecureKeyring _keyring;
    private readonly string _sshDir;

    public SshKeyService(ISecureKeyring? keyring = null, string? sshDir = null)
    {
        _keyring = keyring ?? new SecureKeyring();
        _sshDir = sshDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
    }

    public string SshDirectory => _sshDir;

    /// <summary>Keyring key under which a key's passphrase is stored: <c>sshpass_&lt;sanitized-keypath&gt;</c>.</summary>
    public static string PassphraseKey(string privateKeyPath)
    {
        var normalized = (privateKeyPath ?? string.Empty);
        var sb = new StringBuilder(normalized.Length + 8);
        foreach (var c in normalized)
            sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' ? char.ToLowerInvariant(c) : '_');
        return $"sshpass_{sb}";
    }

    /// <summary>
    /// Builds the <see cref="ProcessStartInfo"/> for <c>ssh-keygen -t ed25519 -f &lt;path&gt;
    /// -N &lt;passphrase&gt; [-C &lt;comment&gt;]</c> using <see cref="ProcessStartInfo.ArgumentList"/>
    /// only. Exposed for the argv-construction test (TI-14 #3) so the exact argv is
    /// asserted without spawning a process. See the class remarks for the security rationale.
    /// </summary>
    internal static ProcessStartInfo BuildKeygenStartInfo(string privateKeyPath, string passphrase, string? comment)
    {
        var psi = new ProcessStartInfo("ssh-keygen")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("ed25519");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(privateKeyPath);
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add(passphrase ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(comment))
        {
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(comment);
        }
        return psi;
    }

    /// <summary>
    /// Generates an ed25519 key pair at <paramref name="privateKeyPath"/> and stores the
    /// (non-empty) passphrase in the keyring. Returns the discovered key info.
    /// </summary>
    public SshKeyInfo Generate(string privateKeyPath, string passphrase, string? comment = null)
    {
        if (string.IsNullOrWhiteSpace(privateKeyPath))
            throw new ArgumentException("A key path is required.", nameof(privateKeyPath));

        var dir = Path.GetDirectoryName(privateKeyPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var psi = BuildKeygenStartInfo(privateKeyPath, passphrase, comment);

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to launch ssh-keygen. Is OpenSSH installed and on the PATH?");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Failed to launch ssh-keygen. Is OpenSSH installed and on the PATH?", ex);
        }

        using (process)
        {
            // Close stdin so any interactive prompt (e.g. overwrite) hits EOF and
            // fails fast rather than hanging the UI thread.
            process.StandardInput.Close();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                // stderr from ssh-keygen never echoes the -N value; still, keep the
                // passphrase out of the message on principle (G-4).
                var detail = string.IsNullOrWhiteSpace(stderr) ? "" : $" {stderr.Trim()}";
                throw new InvalidOperationException($"ssh-keygen failed (exit {process.ExitCode}).{detail}");
            }
        }

        if (!string.IsNullOrEmpty(passphrase))
            _keyring.SaveSecret(PassphraseKey(privateKeyPath), passphrase);

        return Read(privateKeyPath)
            ?? throw new InvalidOperationException("ssh-keygen reported success but the key files were not found.");
    }

    /// <summary>Retrieves a stored passphrase for a key (null if none / keyring miss).</summary>
    public string? GetPassphrase(string privateKeyPath)
        => _keyring.RetrieveSecret(PassphraseKey(privateKeyPath));

    /// <summary>Enumerates key pairs in <c>~/.ssh</c> (each <c>*.pub</c> with a matching private key).</summary>
    public IReadOnlyList<SshKeyInfo> ListKeys()
    {
        if (!Directory.Exists(_sshDir)) return Array.Empty<SshKeyInfo>();

        var keys = new List<SshKeyInfo>();
        foreach (var pub in Directory.EnumerateFiles(_sshDir, "*.pub").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var priv = pub.Substring(0, pub.Length - ".pub".Length);
            if (!File.Exists(priv)) continue; // orphan .pub with no private key — skip
            var info = ReadFromPaths(priv, pub);
            if (info is not null) keys.Add(info);
        }
        return keys;
    }

    /// <summary>Reads a single key pair by private-key path (null if the pair is incomplete).</summary>
    public SshKeyInfo? Read(string privateKeyPath)
    {
        var pub = privateKeyPath + ".pub";
        if (!File.Exists(privateKeyPath) || !File.Exists(pub)) return null;
        return ReadFromPaths(privateKeyPath, pub);
    }

    /// <summary>The public-key text to place on the clipboard (clipboard access is a UI concern).</summary>
    public string CopyPublicKey(SshKeyInfo key) => key.PublicKeyText;

    /// <summary><c>true</c> for scp-like (<c>git@host:…</c>) or <c>ssh://</c> remotes.</summary>
    public static bool IsSshRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) return true;
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;

        // A Windows drive path (C:\repo) is not a remote.
        bool isWindowsDrivePath = url.Length >= 2
            && char.IsLetter(url[0]) && url[1] == ':'
            && (url.Length == 2 || url[2] == '\\' || url[2] == '/');
        if (isWindowsDrivePath) return false;

        // scp-like: [user@]host:path with no scheme and no backslash.
        return url.StartsWith("git@", StringComparison.Ordinal)
            || (!url.Contains("://") && url.Contains(':') && !url.Contains('\\'));
    }

    /// <summary>Reports whether <c>ssh-keygen</c> is available on the PATH.</summary>
    public static bool IsSshKeygenAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ssh-keygen")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Harmless probe: fingerprint a non-existent file — errors out cleanly
                // and (unlike -A) never writes any key. We only care that it launched.
                ArgumentList = { "-l", "-f", Path.Combine(Path.GetTempPath(), "gitloom-nonexistent-probe") },
            });
            // We don't care about the exit code — only that the executable launched.
            p?.WaitForExit(5000);
            return p is not null;
        }
        catch
        {
            return false;
        }
    }

    private static SshKeyInfo? ReadFromPaths(string privateKeyPath, string publicKeyPath)
    {
        string text;
        try { text = File.ReadAllText(publicKeyPath).Trim(); }
        catch { return null; }

        // Public key format: "<type> <base64> [comment]".
        var parts = text.Split(new[] { ' ' }, 3, StringSplitOptions.RemoveEmptyEntries);
        var type = parts.Length > 0 ? parts[0] : string.Empty;
        var comment = parts.Length > 2 ? parts[2].Trim() : string.Empty;

        return new SshKeyInfo
        {
            Name = Path.GetFileName(privateKeyPath),
            PrivateKeyPath = privateKeyPath,
            PublicKeyPath = publicKeyPath,
            PublicKeyText = text,
            KeyType = type,
            Comment = comment,
        };
    }
}
