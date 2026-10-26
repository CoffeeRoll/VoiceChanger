using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(VoiceChanger.App.Tests.TestAppBuilder))]

namespace VoiceChanger.App.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (instead of the no-op headless renderer) so tests can capture screenshots.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();
}
