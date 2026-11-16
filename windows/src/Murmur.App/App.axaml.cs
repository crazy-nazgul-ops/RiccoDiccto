using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Murmur.App.Views;

namespace Murmur.App;

/// <summary>The application.</summary>
public partial class App : Application
{
    private Composition? _composition;
    private MainWindow? _main;

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _composition = Composition.Create();
            _main = new MainWindow(_composition);
            desktop.MainWindow = _main;

            // A second double-click finds the process already running and asks for this window,
            // instead of starting another hook.
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(ShowMain));

            // Closing the window quits. Leaving the process up for a tray icon that often
            // never appeared is how extra copies kept the microphone and the hook, and then
            // each of them typed the same utterance.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
            _main.Closed += (_, _) => desktop.Shutdown();

            // Disposing tears down the keyboard hook and releases the audio device. Leaving
            // a low-level hook installed after exit is the kind of thing that makes a
            // machine feel broken until it is rebooted.
            desktop.ShutdownRequested += (_, _) =>
            {
                // Dispose runs inside the window callback. An exception here is fatal to
                // the process, which skips the rest of shutdown.
                try
                {
                    _composition?.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    System.Diagnostics.Trace.WriteLine(ex);
                }

                _composition = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnTrayShow(object? sender, EventArgs e) => ShowMain();

    private void OnTraySettings(object? sender, EventArgs e)
    {
        ShowMain();
        if (_main is not null && _composition is not null)
        {
            _ = new SettingsWindow(_composition.Settings).ShowDialog(_main);
        }
    }

    private void OnTrayQuit(object? sender, EventArgs e)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private void ShowMain()
    {
        if (_main is null) return;

        _main.Show();
        _main.WindowState = WindowState.Normal;
        _main.Activate();
    }
}
