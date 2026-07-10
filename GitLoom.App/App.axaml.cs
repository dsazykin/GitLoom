using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core;
using GitLoom.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GitLoom.App;

public partial class App : Application
{
    public static ISettingsService Settings { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Load environment variables securely from .env file
        DotNetEnv.Env.TraversePath().Load();

        // Instantiate and load the settings service
        Settings = new SettingsService();

        // Ensure SQLite database is created and migrations are applied. This runs before any window
        // exists, so a bare Migrate() that blocks on a locked database would leave a windowless,
        // dead-looking process (see the single-instance guard in Program.cs). Bound it: if the DB is
        // held by something else we fail loudly and fast instead of hanging invisibly forever.
        try
        {
            var migration = System.Threading.Tasks.Task.Run(() =>
            {
                using var dbContext = new AppDbContext();
                dbContext.Database.Migrate();
            });

            if (!migration.Wait(TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException(
                    "Timed out applying database migrations. Another GitLoom instance may be holding "
                    + "the database lock — close it and relaunch.");
            }

            // Re-throw any exception the migration task captured on this thread.
            migration.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Better a crash with a reason than a silent, windowless hang. LogToTrace / the console
            // will carry this, and the process exits with a non-zero code instead of lingering.
            Console.Error.WriteLine($"[GitLoom] Fatal: database migration failed. {ex.Message}");
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the persisted theme (or the default) before any window opens.
        Theming.ThemeManager.Initialize(Settings.Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
