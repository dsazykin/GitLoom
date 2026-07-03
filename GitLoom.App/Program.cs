using System;
using Avalonia;
using Avalonia.Diagnostics;

namespace GitLoom.App;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "credential")
        {
            // Placeholder for A-1.7
        }
        else if (args.Length >= 3 && args[0] == "--rebase-editor")
        {
            var generatedTodoPath = args[1];
            var gitTodoPath = args[^1];
            try
            {
                System.IO.File.Copy(generatedTodoPath, gitTodoPath, true);
            }
            catch { }
            return;
        }
        else if (args.Length >= 3 && args[0] == "--rebase-msg")
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
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

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

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
