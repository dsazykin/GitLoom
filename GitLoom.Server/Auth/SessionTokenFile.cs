using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace GitLoom.Server.Auth;

/// <summary>
/// Owns the daemon's per-session bearer token and its on-disk home. The token is
/// 256 bits from <see cref="RandomNumberGenerator"/>, written to a file readable
/// only by the current user:
/// <list type="bullet">
///   <item>Linux: <c>~/.gitloom/daemon.token</c>, mode <c>0600</c>.</item>
///   <item>Windows: <c>%LocalAppData%\GitLoom\daemon.token</c>, ACL restricted to the current user.</item>
/// </list>
/// Prints nothing (G-13): a client reads the token from this file, never from stdout.
/// </summary>
public sealed class SessionTokenFile
{
    /// <summary>The absolute path of the token file for this OS/user.</summary>
    public string Path { get; }

    /// <summary>The current session token (hex-encoded 256-bit value).</summary>
    public string Token { get; }

    private SessionTokenFile(string path, string token)
    {
        Path = path;
        Token = token;
    }

    /// <summary>The default per-user token path for the running OS (shared with the client).</summary>
    public static string DefaultPath() => GitLoom.Core.Daemon.DaemonPaths.TokenFilePath();

    /// <summary>
    /// Generates a fresh 256-bit token, writes it user-only-readable to
    /// <paramref name="path"/> (or the OS default), and returns the handle.
    /// </summary>
    public static SessionTokenFile Create(string? path = null)
    {
        path ??= DefaultPath();
        var dir = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        // On Unix, pre-create the file at 0600 so the token never lands under a
        // permissive mode. On Windows the file is created inside the user-scoped
        // %LocalAppData% and the DACL is tightened by path immediately after.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.WriteAllText(path, token);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            RestrictWindows(path);
        }
        else
        {
            // Re-assert 0600 (WriteAllText above may have widened it via umask).
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return new SessionTokenFile(path, token);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        // The current user already owns a file it just created and an owner may always
        // rewrite the DACL by path — so we tighten it here (no SeRestorePrivilege, no
        // owner change needed). Disable inheritance, drop inherited ACEs, grant full
        // control to this user only.
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRule(rule);
        }

        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
