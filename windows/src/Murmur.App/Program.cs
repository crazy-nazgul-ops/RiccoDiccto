using Avalonia;

namespace Murmur.App;

/// <summary>Entry point.</summary>
public static class Program
{
    /// <summary>Starts the app, or runs a headless self-test.</summary>
    /// <param name="args">Command line. <c>--selftest</c> exits without showing UI.</param>
    /// <returns>0 on success.</returns>
    [STAThread]
    public static int Main(string[] args)
    {
        PlatformFactory.InstallResolver();

        // The published single-file exe is the only artifact CI can run end to end, and a
        // GitHub runner cannot show a window. This branch exercises startup — assembly
        // loading, native library resolution out of the self-extracted bundle, model
        // discovery — and exits, which is the class of failure that only appears after
        // publishing.
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            return SelfTest.Run();
        }

        if (args.Contains("--capture-smoke", StringComparer.OrdinalIgnoreCase))
        {
            return CaptureSmoke.Run();
        }

        // Self-test and capture-smoke must not take this lock: CI runs them while nothing
        // else is open, and a developer may run them against an already-running app.
        if (!SingleInstance.TryAcquire(out var instance) || instance is null)
        {
            SingleInstance.SignalShow();
            return 0;
        }

        using (instance)
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
    }

    /// <summary>Configures Avalonia. Also used by the headless test host.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
