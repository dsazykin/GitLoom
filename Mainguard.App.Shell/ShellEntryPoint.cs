using System;
using System.Threading;
using Avalonia;
using Mainguard.Git;

namespace Mainguard.App.Shell;

/// <summary>
/// The shared entry-point plumbing both exe heads (Mainguard.Client.App / Mainguard.Pro.App) call from
/// their thin <c>Main</c> (step 2f). Each head first sets <see cref="App.Edition"/> (+ the Pro head its
/// composition seams), then delegates the edition-agnostic parts here: the git-editor / credential
/// self-invocation shims (which must run and return BEFORE anything else), the single-instance guard, and
/// the Avalonia app build. Keeping this in the shell means the two heads never duplicate the shim logic —
/// and the shims are the app invoking ITSELF, so both heads must expose them identically.
/// </summary>
public static class ShellEntryPoint
{
    /// <summary>
    /// Handles the app's self-invocation shims — the interactive-rebase todo/message editors and the
    /// credential-helper placeholder — that Git launches this executable to perform. Returns <c>true</c>
    /// when the process WAS such an invocation (it has done its work; the caller's <c>Main</c> must return
    /// immediately, before the single-instance guard, so the app's own rebase/credential calls of itself are
    /// never blocked). Returns <c>false</c> for an ordinary launch.
    /// </summary>
    public static bool TryHandleShim(string[] args)
    {
        if (args.Length >= 2 && args[0] == "credential")
        {
            // Placeholder for A-1.7
            return true;
        }

        if (args.Length >= 3 && args[0] == "--rebase-editor")
        {
            var generatedTodoPath = args[1];
            var gitTodoPath = args[^1];
            try
            {
                // Invariant 5: log the todo actually applied to git's sequence file.
                try
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Mainguard] Interactive rebase applied todo:\n"
                        + System.IO.File.ReadAllText(generatedTodoPath));
                }
                catch { }
                System.IO.File.Copy(generatedTodoPath, gitTodoPath, true);
            }
            catch { }
            return true;
        }

        if (args.Length >= 3 && args[0] == "--rebase-msg")
        {
            // git invokes GIT_EDITOR once per reword and once per squash *chain*, passing the
            // message file (e.g. .git/COMMIT_EDITMSG). We learn which commit git is currently
            // editing from the last line of .git/rebase-merge/done and copy in the message we
            // staged for that SHA. If we staged nothing, we exit 0 and leave git's default.
            var msgDir = args[1];
            var gitMsgPath = args[^1];
            try
            {
                var sha = ReadCurrentRebaseSha(gitMsgPath);
                if (sha != null && System.IO.Directory.Exists(msgDir))
                {
                    var msgFile = System.IO.Path.Combine(msgDir, sha + ".msg");
                    if (!System.IO.File.Exists(msgFile))
                    {
                        // Tolerate abbreviated SHAs on either side.
                        foreach (var f in System.IO.Directory.GetFiles(msgDir, "*.msg"))
                        {
                            var key = System.IO.Path.GetFileNameWithoutExtension(f);
                            if (key.StartsWith(sha, StringComparison.OrdinalIgnoreCase)
                                || sha.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                            {
                                msgFile = f;
                                break;
                            }
                        }
                    }
                    if (System.IO.File.Exists(msgFile))
                        System.IO.File.Copy(msgFile, gitMsgPath, true);
                }
            }
            catch { }
            return true;
        }

        return false;
    }

    /// <summary>
    /// The ordinary launch: a single-instance guard (two live Mainguard processes would contend for the
    /// SQLite database lock and the second would hang forever on startup migration — the exact bug that
    /// leaves a dead-looking, windowless process), then the classic desktop lifetime. A killed instance
    /// frees the mutex automatically, so a crash never wedges the next launch. Call this AFTER
    /// <see cref="TryHandleShim"/> returned false and the head has selected its edition.
    /// </summary>
    public static void RunDesktop(string[] args)
    {
        using var singleInstance = new Mutex(initiallyOwned: true, "Mainguard.App.SingleInstance", out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            Console.Error.WriteLine("Mainguard is already running.");
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    // Reads the original SHA of the rebase step git is currently editing from the last
    // executed line of .git/rebase-merge/done. The git directory is derived from the
    // message-file path git handed us, so this is correct even for linked worktrees.
    private static string? ReadCurrentRebaseSha(string gitMsgPath)
    {
        var donePath = FindRebaseDone(gitMsgPath);
        if (donePath == null) return null;

        string[] lines;
        try { lines = System.IO.File.ReadAllLines(donePath); }
        catch { return null; }

        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            // done lines look like: "<command> <sha> <subject...>"
            return parts.Length >= 2 ? parts[1] : null;
        }
        return null;
    }

    private static string? FindRebaseDone(string gitMsgPath)
    {
        try
        {
            var dir = new System.IO.DirectoryInfo(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(gitMsgPath))!);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "rebase-merge", StringComparison.Ordinal))
                {
                    var here = System.IO.Path.Combine(dir.FullName, "done");
                    if (System.IO.File.Exists(here)) return here;
                }
                var nested = System.IO.Path.Combine(dir.FullName, "rebase-merge", "done");
                if (System.IO.File.Exists(nested)) return nested;
                dir = dir.Parent;
            }
        }
        catch { }
        return null;
    }
}
