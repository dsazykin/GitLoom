using Avalonia;
using Avalonia.Headless;
using GitLoom.Tests.Headless;

// TI-00: headless Avalonia test application. Renders offscreen with Skia (UseHeadlessDrawing=false)
// so [AvaloniaFact] tests can drive real ViewModels/Views and capture rendered frames — no display.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace GitLoom.Tests.Headless;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<GitLoom.App.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
