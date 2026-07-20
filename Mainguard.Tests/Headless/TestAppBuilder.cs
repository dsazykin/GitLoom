using Avalonia;
using Avalonia.Headless;
using Mainguard.Tests.Headless;

// TI-00: headless Avalonia test application. Renders offscreen with Skia (UseHeadlessDrawing=false)
// so [AvaloniaFact] tests can drive real ViewModels/Views and capture rendered frames — no display.
[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Mainguard.Tests.Headless;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Mainguard.App.Shell.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
