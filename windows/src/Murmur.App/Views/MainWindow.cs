using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Murmur.App.Controls;
using Murmur.App.Design;
using Murmur.Core;
using Murmur.Speech;

namespace Murmur.App.Views;

/// <summary>
/// The main window — the front panel of the unit.
/// </summary>
/// <remarks>
/// <para>
/// Laid out the way a deck is: transport and meter across the top on the panel itself, then a
/// recessed well below holding whichever section is selected.
/// </para>
/// <para>
/// Built in code rather than XAML, deliberately. Every value comes from <see cref="Tokens"/>,
/// and XAML makes it far too easy to type a literal <c>Margin="12,8"</c> that silently escapes
/// the design system. In C# a stray number is visible in review.
/// </para>
/// </remarks>
public sealed class MainWindow : Window
{
    private readonly Composition? _composition;
    private readonly TransportKey _recordKey;
    private readonly Lamp _recordLamp;
    private readonly VuMeter _meter;
    private readonly TextBlock _counter;
    private readonly ContentControl _sectionHost;
    private readonly TransportKey _transcriptionsKey;
    private readonly TransportKey _dictionaryKey;
    private readonly DispatcherTimer _counterTimer;

    private readonly TransportKey _panelKey;
    private Control? _transcriptionsView;
    private Control? _dictionaryView;
    private Control? _body;
    private DateTimeOffset? _startedAt;
    private double _restHeight;
    private double _restMinHeight;
    private bool _folded;
    private bool _panelOpen = true;
    private bool _wasCapturing;
    private bool _releasedTopmost;
    private bool _settingsOpen;

    /// <summary>Builds a window with no engine behind it. Used by headless tests.</summary>
    public MainWindow() : this(null) { }

    /// <summary>Builds the window over <paramref name="composition"/>.</summary>
    public MainWindow(Composition? composition)
    {
        _composition = composition;

        Title = "RiccoDiccto";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Murmur.App/Assets/tray.ico")));
        MinWidth = 720;
        MinHeight = 520;
        Width = 880;
        Height = 640;
        _restHeight = Height;
        _restMinHeight = MinHeight;
        Background = Tokens.Brushes.Chassis;

        Activated += (_, _) =>
        {
            _releasedTopmost = false;
            ApplyChrome();
        };
        Deactivated += (_, _) =>
        {
            // A click on the window behind has to be able to raise it. Forcing Topmost
            // back on the next tick would swallow that click for the rest of the take.
            if (IsCapturing) _releasedTopmost = true;
            ApplyChrome();
        };

        _recordKey = new TransportKey { Content = "RECORD", IsDesertFace = true };
        _recordKey.Click += (_, _) => ToggleRecording();

        _recordLamp = new Lamp { LampColor = Tokens.Colors.Record };
        _meter = new VuMeter { Width = 168, Height = 54 };

        _counter = new TextBlock
        {
            Text = "00:00",
            FontFamily = Tokens.Fonts.Mono,
            FontSize = Tokens.Fonts.CounterLarge,
            Foreground = Tokens.Brushes.InkOnDeck,
        };

        _transcriptionsKey = new TransportKey { Content = "TRANSCRIPTIONS", IsEngaged = true };
        _dictionaryKey = new TransportKey { Content = "DICTIONARY" };
        _panelKey = new TransportKey { Content = "CLOSE", IsDesertFace = true };
        _panelKey.Click += (_, _) =>
        {
            _panelOpen = !_panelOpen;
            ApplyChrome();
        };
        _transcriptionsKey.Click += (_, _) => ShowSection(transcriptions: true);
        _dictionaryKey.Click += (_, _) => ShowSection(transcriptions: false);

        _sectionHost = new ContentControl();

        // The meter and counter are polled rather than pushed. The engine raises Changed on
        // a background thread at buffer rate, and marshalling every one of those to the UI
        // thread would be far more traffic than a display refresh needs.
        _counterTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _counterTimer.Tick += (_, _) => SyncFromEngine();
        _counterTimer.Start();

        Content = BuildLayout();
        ShowSection(transcriptions: true);

        if (_composition?.Engine is not null) _composition.Engine.Start();
    }

    private DockPanel BuildLayout()
    {
        var root = new DockPanel { Margin = new Thickness(Tokens.Space.Roomy) };

        root.Children.Add(Panels.Docked(BuildTransportPanel(), Dock.Top));

        var body = new DockPanel();
        body.Children.Add(Panels.Docked(BuildSectionKeys(), Dock.Top));
        if (_composition is not null && !ModelIsReady(_composition))
        {
            body.Children.Add(Panels.Docked(BuildModelBanner(), Dock.Top));
        }

        body.Children.Add(BuildWell(_sectionHost));
        _body = body;
        root.Children.Add(body);
        return root;
    }

    /// <summary>Record/stop, the record lamp, the level meter and the tape counter.</summary>
    private BrushedPanel BuildTransportPanel()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Wide,
            Margin = new Thickness(Tokens.Space.Roomy),
        };

        row.Children.Add(Panels.Labelled("TRANSPORT", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Snug,
            Children =
            {
                _recordKey,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = Tokens.Space.Tight,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        _recordLamp,
                        new Silkscreen { Text = "REC", Foreground = Tokens.Brushes.TransportBrass },
                    },
                },
            },
        }, Tokens.Brushes.TransportBrass));

        row.Children.Add(Panels.Labelled("LEVEL", _meter, Tokens.Brushes.TransportBrass));
        row.Children.Add(Panels.Labelled("COUNTER", new ArchedReadout { Child = _counter }, Tokens.Brushes.TransportBrass));

        var settings = new TransportKey { Content = "SETTINGS", IsDesertFace = true };
        settings.Click += (_, _) => ShowSettings();

        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            VerticalAlignment = VerticalAlignment.Bottom,
            Children =
            {
                _panelKey,
                settings,
                new Vents { Count = 8, VerticalAlignment = VerticalAlignment.Center },
            },
        });

        return new BrushedPanel
        {
            IsTransportFace = true,
            Child = row,
            Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
        };
    }

    private StackPanel BuildSectionKeys() => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = Tokens.Space.Snug,
        Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
        Children = { _transcriptionsKey, _dictionaryKey },
    };

    /// <summary>
    /// Whether a complete Parakeet model is available to this session.
    /// </summary>
    /// <remarks>
    /// <see cref="Composition.IsModelInstalled"/> only searches the default locations.
    /// A model kept somewhere else is still usable when <see cref="SettingsData.ModelDirectory"/>
    /// points at a complete folder, which is the path the engine loads.
    /// </remarks>
    private static bool ModelIsReady(Composition composition)
    {
        var configured = composition.Settings.Data.ModelDirectory;
        return configured is not null && ParakeetTranscriber.IsComplete(configured)
            || Composition.IsModelInstalled;
    }

    /// <summary>
    /// A standing notice that the app cannot transcribe yet.
    /// </summary>
    /// <remarks>
    /// Unlike macOS, Windows has no built-in engine to fall back on, so a missing model means
    /// the app does nothing at all. That has to be visible on the front panel rather than
    /// buried in Settings.
    /// </remarks>
    private static BrushedPanel BuildModelBanner() => new()
    {
        Margin = new Thickness(0, 0, 0, Tokens.Space.Base),
        Child = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Tokens.Space.Base,
            Margin = new Thickness(Tokens.Space.Base),
            Children =
            {
                new Lamp
                {
                    IsLit = true,
                    LampColor = Tokens.Colors.MeterAmber,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                new TextBlock
                {
                    Text = "Speech model not installed — RiccoDiccto cannot transcribe yet. "
                         + "See Settings, or docs/PARAKEET-WINDOWS.md.",
                    FontFamily = Tokens.Fonts.Grotesque,
                    FontSize = Tokens.Fonts.Label,
                    Foreground = Tokens.Brushes.Ink,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        },
    };

    /// <summary>A recessed well cut into the panel — content sits inside it.</summary>
    private static Border BuildWell(Control content) => new()
    {
        Background = Tokens.Brushes.Well,
        CornerRadius = new CornerRadius(Tokens.Radius.Panel),
        BorderBrush = new SolidColorBrush(Tokens.Colors.Seam, 0.55),
        BorderThickness = new Thickness(Tokens.Border.Hairline),
        Padding = new Thickness(Tokens.Space.Hair),
        Child = content,
    };

    private void ShowSection(bool transcriptions)
    {
        _transcriptionsKey.IsEngaged = transcriptions;
        _dictionaryKey.IsEngaged = !transcriptions;

        if (_composition is null)
        {
            _sectionHost.Content = Panels.EmptyState(
                transcriptions ? "NO RECORDINGS" : "DICTIONARY EMPTY",
                transcriptions ? "Press Record to start." : "Add words it keeps getting wrong.");
            return;
        }

        // Built once and reused: rebuilding would drop the user's search text every time
        // they switched tabs.
        if (transcriptions)
        {
            _transcriptionsView ??= new TranscriptionsView(_composition.Transcripts);
            _sectionHost.Content = _transcriptionsView;
        }
        else
        {
            _dictionaryView ??= new DictionaryView(_composition.Dictionary);
            _sectionHost.Content = _dictionaryView;
        }
    }

    private void ShowSettings()
    {
        if (_composition is null) return;

        // The dialog takes focus. Folding the owner underneath it would hide the page
        // the user just opened Settings from.
        _settingsOpen = true;
        var dialog = new SettingsWindow(_composition.Settings);
        dialog.Closed += (_, _) =>
        {
            _settingsOpen = false;
            ApplyChrome();
        };
        _ = dialog.ShowDialog(this);
    }

    /// <summary>The microphone is open. Transcription after the take does not count.</summary>
    private bool IsCapturing =>
        _composition?.Engine is { } engine
            ? engine.State == DictationState.Recording
            : IsRecording;

    /// <summary>
    /// Starting a take closes the lower panel. Stopping opens it again.
    /// A click on the panel key does the same thing on its own.
    /// </summary>
    private void NoteCaptureEdge()
    {
        var capturing = IsCapturing;
        if (capturing == _wasCapturing) return;

        _wasCapturing = capturing;
        _panelOpen = !capturing;
        if (capturing) _releasedTopmost = false;
    }

    /// <summary>
    /// The transport strip stays above other windows only while audio is being captured,
    /// and only until the user clicks the window behind it.
    /// </summary>
    private void ApplyChrome()
    {
        NoteCaptureEdge();

        var stayOnTop = IsCapturing
            && !_releasedTopmost
            && _composition?.Settings.Data.StayOnTopWhileRecording == true;
        if (Topmost != stayOnTop) Topmost = stayOnTop;

        _panelKey.Content = _panelOpen ? "CLOSE" : "OPEN";

        // The dialog takes focus from this window. Shrinking the owner underneath it
        // would hide the page the user just opened Settings from.
        if (_settingsOpen) return;

        var fold = !_panelOpen;
        if (_body is not null) _body.IsVisible = !fold;
        if (fold == _folded) return;

        _folded = fold;
        Background = fold ? Tokens.Brushes.TransportChassis : Tokens.Brushes.Chassis;

        if (fold)
        {
            _restHeight = Height;
            MinHeight = 0;
            CanResize = false;
            SizeToContent = SizeToContent.Height;
            return;
        }

        SizeToContent = SizeToContent.Manual;
        CanResize = true;
        MinHeight = _restMinHeight;
        Height = _restHeight;
    }

    /// <summary>Pulls state from the engine onto the panel.</summary>
    private void SyncFromEngine()
    {
        var engine = _composition?.Engine;
        if (engine is null)
        {
            if (_startedAt is not null) UpdateCounter();
            return;
        }

        engine.NoteAlive();
        var recording = engine.State != DictationState.Idle;

        _meter.Level = MeterLevel(engine.Level);
        _meter.IsActive = recording;
        _recordLamp.IsLit = engine.State == DictationState.Recording;
        _recordKey.IsEngaged = recording;
        _recordKey.Content = recording ? "STOP" : "RECORD";

        if (recording && _startedAt is null) _startedAt = DateTimeOffset.Now;
        else if (!recording) _startedAt = null;

        ApplyChrome();

        UpdateCounter();
    }

    /// <summary>
    /// Maps a linear RMS to the meter's sweep.
    /// </summary>
    /// <remarks>
    /// The face is −72…0 dBFS, so a soft voice around −45 dBFS sits clearly off the rest
    /// instead of looking like silence.
    /// </remarks>
    private static double MeterLevel(float rms)
    {
        if (rms < 1e-6f) return 0;

        var db = 20 * Math.Log10(rms);
        return Math.Clamp((db + 72) / 72, 0, 1);
    }

    private void UpdateCounter()
    {
        var elapsed = _startedAt is null ? TimeSpan.Zero : DateTimeOffset.Now - _startedAt.Value;
        _counter.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}");
    }

    /// <summary>Toggles the transport. Exposed for headless tests.</summary>
    public void ToggleRecording()
    {
        // With no engine — a headless test, or a machine with no platform layer — the panel
        // still toggles so the visual state can be exercised.
        if (_composition?.Engine is null)
        {
            IsRecording = !IsRecording;
            _recordKey.Content = IsRecording ? "STOP" : "RECORD";
            _recordKey.IsEngaged = IsRecording;
            _recordLamp.IsLit = IsRecording;
            _meter.IsActive = IsRecording;
            _startedAt = IsRecording ? DateTimeOffset.Now : null;
            ApplyChrome();
            return;
        }

        // The button is a convenience; the hotkey is the real trigger. Both funnel through
        // the same engine so there is only ever one state machine.
        _composition.Engine.TogglePushToTalk();
        SyncFromEngine();
    }

    /// <summary>Whether the transport is engaged. Exposed for headless tests.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>The record lamp. Exposed for headless tests.</summary>
    public Lamp RecordLamp => _recordLamp;

    /// <summary>The level meter. Exposed for headless tests.</summary>
    public VuMeter Meter => _meter;

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _counterTimer.Stop();
        base.OnClosed(e);
    }
}
