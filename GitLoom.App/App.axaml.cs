using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core;
using GitLoom.Core.Services;

namespace GitLoom.App;

public partial class App : Application
{
    public static ISettingsService Settings { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Instantiate and load the settings service
        Settings = new SettingsService();

        // Ensure SQLite database is created and seeded
        using (var dbContext = new AppDbContext())
        {
            dbContext.Database.EnsureCreated();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
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