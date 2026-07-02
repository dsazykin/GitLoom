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
            var msgDir = args[1];
            var gitMsgPath = args[^1];
            try
            {
                var files = System.IO.Directory.GetFiles(msgDir);
                Array.Sort(files);
                if (files.Length > 0)
                {
                    var firstMsg = files[0];
                    System.IO.File.Copy(firstMsg, gitMsgPath, true);
                    System.IO.File.Delete(firstMsg);
                }
            }
            catch { }
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
}
