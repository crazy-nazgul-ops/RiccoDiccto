using System.Text.RegularExpressions;
using System.Threading.Channels;
using Murmur.Abstractions;
using Murmur.Dictionary;

namespace Murmur.Core;

/// <summary>What the engine is doing right now.</summary>
public enum DictationState
{
    /// <summary>Waiting for the hotkey.</summary>
    Idle,

    /// <summary>The key is held; audio is being captured.</summary>
    Recording,

    /// <summary>The key is released; the utterance is being transcribed.</summary>
    Transcribing,
}

/// <summary>One completed dictation.</summary>
public sealed record DictationResult(
    DateTimeOffset At,
    TimeSpan AudioDuration,
    TimeSpan ProcessingTime,
    string Text,
    IReadOnlyList<AppliedCorrection> Corrections);

/// <summary>
/// The whole dictation flow: hotkey, capture, hotkey again, transcribe, correct, inject.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately platform-neutral. It targets plain <c>net10.0</c>, so <c>CA1416</c> turns any
/// accidental Windows API call in here into a build error. Everything platform-specific
/// arrives through the four interfaces it is constructed with.
/// </para>
/// <para>
/// That is what makes the interesting behaviour testable without Windows: hand it fakes and
/// the entire path — including chunking, the correction pass and the "nothing was said"
/// case — runs on any machine, in milliseconds.
/// </para>
/// </remarks>
public sealed class DictationEngine : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly IHotkeySource _hotkey;
    private readonly ITranscriber _transcriber;
    private readonly ITextInjector _injector;
    private readonly IClock _clock;
    private readonly Func<IReadOnlyList<DictionaryEntry>> _dictionary;
    private readonly ISessionCue? _cues;
    private readonly IRecordCue? _recordCue;
    private int _awaitingReadyCue;

    /// <summary>No capture chunk for this long means the microphone side has stalled.</summary>
    private static readonly TimeSpan AudioStall = TimeSpan.FromSeconds(5);

    /// <summary>No sign of life from the window for this long means the app has stopped responding.</summary>
    private static readonly TimeSpan UiStall = TimeSpan.FromSeconds(6);

    /// <summary>How long the input must stay quiet before the phrase so far is transcribed.</summary>
    private static readonly int PauseSamples = AudioChunk.SampleRate;

    /// <summary>Shorter than this is a click, not a phrase, and is not sent on its own.</summary>
    private static readonly int MinimumSpeechSamples = AudioChunk.SampleRate / 5;

    /// <summary>
    /// A chunk at least this loud counts as speech.
    /// </summary>
    /// <remarks>
    /// About −50 dBFS. A quiet room on a headset sits nearer −60 dBFS, and a soft voice is
    /// often only a little above that. The previous −40 dBFS gate was dropping those words
    /// before they ever reached the model.
    /// </remarks>
    private const float SpeechRms = 0.003f;

    /// <summary>Quiet phrases are raised to about this level before transcription.</summary>
    private const float TargetSpeechRms = 0.05f;

    /// <summary>Upper bound on that lift, so a noise floor is not turned into a shout.</summary>
    private const float MaxQuietGain = 16f;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _recording;
    private List<float>? _buffer;
    private int _speechSamples;
    private int _silenceSamples;
    private Channel<Utterance>? _pending;
    private Task? _transcription;
    private DateTimeOffset _startedAt;
    private long _lastAudioTicks;
    private long _lastUiTicks;
    private int _sawUi;
    private int _voiceStop;
    private int _interrupted;

    private readonly record struct Utterance(List<float> Samples, bool TrailingSpace);

    /// <summary>Current state.</summary>
    public DictationState State { get; private set; } = DictationState.Idle;

    /// <summary>Most recent input level, 0…1. Drives the meter.</summary>
    public float Level { get; private set; }

    /// <summary>Raised when a dictation completes and produced text.</summary>
    public event EventHandler<DictationResult>? Completed;

    /// <summary>Raised whenever <see cref="State"/> or <see cref="Level"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Wires the engine to its platform implementations.</summary>
    /// <param name="capture">Microphone source.</param>
    /// <param name="hotkey">Push-to-talk source.</param>
    /// <param name="transcriber">Speech engine.</param>
    /// <param name="injector">Where finished text goes.</param>
    /// <param name="dictionary">
    /// Read fresh on every utterance rather than captured once, so edits take effect without
    /// a restart.
    /// </param>
    /// <param name="clock">Time source; defaults to the system clock.</param>
    /// <param name="cues">Stop and failure sounds. Omitted in tests that do not care.</param>
    /// <param name="recordCue">
    /// Armed and model-ready sounds. Omitted in tests that do not care.
    /// </param>
    public DictationEngine(
        IAudioCapture capture,
        IHotkeySource hotkey,
        ITranscriber transcriber,
        ITextInjector injector,
        Func<IReadOnlyList<DictionaryEntry>> dictionary,
        IClock? clock = null,
        ISessionCue? cues = null,
        IRecordCue? recordCue = null)
    {
        _capture = capture;
        _hotkey = hotkey;
        _transcriber = transcriber;
        _injector = injector;
        _dictionary = dictionary;
        _cues = cues;
        _recordCue = recordCue;
        _clock = clock ?? SystemClock.Instance;

        _hotkey.Pressed += OnPressed;
    }

    /// <summary>Arms the hotkey.</summary>
    /// <returns>False if the hook could not be installed.</returns>
    public bool Start() => _hotkey.Start();

    /// <summary>
    /// Starts recording, or stops it if it is already running.
    /// </summary>
    /// <remarks>
    /// The hotkey and the on-screen key both come through here. Letting go of the hotkey
    /// does not stop a take — a second press does.
    /// </remarks>
    public void TogglePushToTalk()
    {
        if (State == DictationState.Idle) _ = BeginAsync();
        else if (State == DictationState.Recording) _ = EndAsync();
    }

    private void OnPressed(object? sender, EventArgs e) => TogglePushToTalk();

    /// <summary>Tells the stall watch that the window is still responding.</summary>
    public void NoteAlive()
    {
        _sawUi = 1;
        Interlocked.Exchange(ref _lastUiTicks, _clock.Now.UtcTicks);
    }

    private async Task BeginAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Idle) return;

            _buffer = [];
            _speechSamples = 0;
            _silenceSamples = 0;
            _pending = Channel.CreateUnbounded<Utterance>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            _transcription = TranscribeQueuedAsync(_pending.Reader);
            _startedAt = _clock.Now;
            _voiceStop = 0;
            _interrupted = 0;
            TouchAudio();
            _cues?.SetRecording(true);
            _recording = new CancellationTokenSource();
            var watch = WatchAsync(_recording.Token);
            SetState(DictationState.Recording);
            _ = watch;
            if (_recordCue is not null) _ = Task.Run(AnnounceReadyAsync);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await foreach (var chunk in _capture.CaptureAsync(_recording!.Token).ConfigureAwait(false))
            {
                // Stop consuming the moment recording ends. Cancellation is cooperative, so
                // chunks already queued still arrive after EndAsync has moved on — and
                // without this guard one of them sets Level back to a reading that has
                // already been zeroed.
                if (State != DictationState.Recording) break;

                var rms = chunk.Rms();
                TouchAudio();
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (State != DictationState.Recording || _buffer is null) break;

                    // Leading quiet is not part of a phrase. Once speech has started, quiet
                    // is kept until it lasts long enough to count as a pause.
                    if (rms < SpeechRms && _speechSamples == 0) 
                    {
                        Level = rms;
                    }
                    else
                    {
                        _buffer.AddRange(chunk.Samples.Span);
                        if (rms >= SpeechRms)
                        {
                            _speechSamples += chunk.Samples.Length;
                            _silenceSamples = 0;
                        }
                        else
                        {
                            _silenceSamples += chunk.Samples.Length;
                            if (_silenceSamples >= PauseSamples)
                            {
                                if (_speechSamples >= MinimumSpeechSamples)
                                {
                                    _pending!.Writer.TryWrite(new Utterance(_buffer, TrailingSpace: true));
                                }

                                _buffer = [];
                                _speechSamples = 0;
                                _silenceSamples = 0;
                            }
                        }
                    }

                    Level = rms;
                }
                finally
                {
                    _gate.Release();
                }

                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the key was released.
        }
        catch (Exception ex)
        {
            // Capture failed before any audio arrived. Leave the engine idle, otherwise the
            // button stays on STOP and every later click only tries to finish an empty take.
            Remember(ex);
            SignalInterrupted();
            if (_recording is not null) await _recording.CancelAsync().ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (State == DictationState.Recording)
                {
                    _buffer = null;
                    _pending?.Writer.TryComplete();
                    Level = 0;
                    SetState(DictationState.Idle);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            // Authoritative: this runs only once the capture loop has genuinely finished, so
            // nothing can raise the level afterwards and leave the meter stuck.
            Level = 0;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task EndAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != DictationState.Recording) return;

            await _recording!.CancelAsync().ConfigureAwait(false);
            // A deliberate stop sends whatever was said, however short. The minimum length
            // applies only to a pause, so a click in a quiet room is not transcribed on its own.
            if (_buffer is { Count: > 0 } && _speechSamples > 0 && _pending is not null)
            {
                _pending.Writer.TryWrite(new Utterance(_buffer, TrailingSpace: false));
            }

            _buffer = null;
            _speechSamples = 0;
            _silenceSamples = 0;
            _pending?.Writer.TryComplete();
            Level = 0;
            SetState(DictationState.Transcribing);
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            if (_transcription is not null) await _transcription.ConfigureAwait(false);
        }
        finally
        {
            _recording?.Dispose();
            _recording = null;
            _cues?.SetRecording(false);
            SetState(DictationState.Idle);
            if (Interlocked.Exchange(ref _voiceStop, 0) == 1 && Volatile.Read(ref _interrupted) == 0)
            {
                _cues?.PlayStopped();
            }
        }
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                if (State != DictationState.Recording) continue;

                _cues?.Pulse();
                var now = _clock.Now.UtcTicks;
                var audioStalled = now - Interlocked.Read(ref _lastAudioTicks) > AudioStall.Ticks;
                var uiFrozen = _sawUi == 1 && now - Interlocked.Read(ref _lastUiTicks) > UiStall.Ticks;
                if (audioStalled || uiFrozen) SignalInterrupted();
            }
        }
        catch (OperationCanceledException)
        {
            // The take ended.
        }
    }

    /// <summary>
    /// Plays the armed tick immediately, then the ready tick once the model is actually loaded.
    /// </summary>
    /// <remarks>
    /// A cold start loads while the take is already rolling, so the second tick waits.
    /// A later take finds the model in memory and plays the two ticks back to back.
    /// </remarks>
    private async Task AnnounceReadyAsync()
    {
        try
        {
            _recordCue?.PlayRecording();

            if (_transcriber.IsReady)
            {
                _recordCue?.PlayModelReady();
                return;
            }

            // A second press during the load already played its armed tick. One waiter
            // plays the ready tick, so the two presses do not each announce the load.
            if (Interlocked.Exchange(ref _awaitingReadyCue, 1) != 0) return;

            var loaded = await _transcriber.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _awaitingReadyCue, 0);
            if (loaded) _recordCue?.PlayModelReady();
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _awaitingReadyCue, 0);
            Remember(ex);
        }
    }

    private void TouchAudio() => Interlocked.Exchange(ref _lastAudioTicks, _clock.Now.UtcTicks);

    /// <summary>Stops a take that the user ended by saying "stop recording".</summary>
    private void RequestVoiceStop()
    {
        Interlocked.Exchange(ref _voiceStop, 1);
        if (State == DictationState.Recording) _ = EndAsync();
    }

    /// <summary>The take failed or the app stopped responding. One cue per take.</summary>
    private void SignalInterrupted()
    {
        if (Interlocked.Exchange(ref _interrupted, 1) != 0) return;

        _cues?.SetRecording(false);
        _cues?.PlayInterrupted();
        if (State == DictationState.Recording) _ = EndAsync();
    }

    private async Task TranscribeQueuedAsync(ChannelReader<Utterance> pending)
    {
        await foreach (var utterance in pending.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(utterance).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Remember(ex);
            }
        }
    }

    private async Task ProcessAsync(Utterance utterance)
    {
        var samples = utterance.Samples;
        if (samples.Count == 0) return;

        // The model is not loaded at startup. Without this, TranscribeAsync returns an empty
        // string, Completed never fires, and no history file is written.
        if (!_transcriber.IsReady)
        {
            var loaded = await _transcriber.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            if (!loaded) return;
        }

        // Measured from key release, because that is the wait the user actually feels — and
        // it is the only figure on which a streaming and a batch engine compare honestly.
        var releasedAt = _clock.Now;
        var audio = new ReadOnlyMemory<float>(LiftQuietSpeech(samples));

        var entries = _dictionary();
        var bias = DictionaryCorrector.BiasPhrases(entries);

        var pieces = AudioSegmenter.Split(audio);
        var transcripts = new List<string>(pieces.Count);

        foreach (var piece in pieces)
        {
            var text = await _transcriber
                .TranscribeAsync(piece, bias, CancellationToken.None)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(text)) transcripts.Add(text.Trim());
        }

        var raw = string.Join(' ', transcripts);
        if (string.IsNullOrWhiteSpace(raw)) return;

        // The dictionary runs last and unconditionally. Biasing only raises the odds of the
        // right word; this is the pass that guarantees it.
        var (corrected, applied) = new DictionaryCorrector(entries).Apply(raw);
        var commanded = ApplyStopCommand(ApplyLineBreakCommands(corrected), out var stop);
        if (stop) RequestVoiceStop();
        if (commanded.Length == 0) return;

        var result = new DictationResult(
            At: releasedAt,
            AudioDuration: TimeSpan.FromSeconds((double)audio.Length / AudioChunk.SampleRate),
            ProcessingTime: _clock.Now - releasedAt,
            Text: commanded,
            Corrections: applied);

        Completed?.Invoke(this, result);

        var typed = utterance.TrailingSpace
            && !commanded.EndsWith(' ')
            && !commanded.EndsWith('\n')
            && !commanded.EndsWith('\v')
            ? commanded + " "
            : commanded;
        await _injector.InjectAsync(typed, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns "line break" into one newline and "line gap" into a vertical tab.
    /// </summary>
    /// <remarks>
    /// The injector sends Enter for a newline and two Enters for a vertical tab. "line break
    /// line break" stays two separate breaks, so it does not become a line gap. "line breaks"
    /// and "line gaps" are left alone.
    /// </remarks>
    private static string ApplyLineBreakCommands(string text)
    {
        if (!text.Contains("line", StringComparison.OrdinalIgnoreCase)) return text;

        var replaced = LineGapCommand.Replace(text, "\v");
        replaced = LineBreakCommand.Replace(replaced, "\n");
        replaced = SpacesAroundLineBreaks.Replace(replaced, "$1");
        return replaced.Trim(' ');
    }

    private static readonly Regex LineGapCommand = new(
        @"\bline[ \t-]*gap\b[.,!?;:]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LineBreakCommand = new(
        @"\bline[ \t-]*break\b[.,!?;:]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SpacesAroundLineBreaks = new(
        @"[^\S\n\v]*(\n|\v)[^\S\n\v]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Removes a spoken "stop recording". The caller ends the take; the words are not typed.
    /// </summary>
    private static string ApplyStopCommand(string text, out bool stop)
    {
        stop = false;
        if (!text.Contains("stop", StringComparison.OrdinalIgnoreCase)) return text;

        var replaced = StopRecordingCommand.Replace(text, " ");
        stop = !string.Equals(replaced, text, StringComparison.Ordinal);
        if (!stop) return text;

        replaced = ExtraSpaces.Replace(replaced, " ");
        return replaced.Trim(' ');
    }

    private static readonly Regex StopRecordingCommand = new(
        @"\bstop[ \t-]*recording\b[.,!?;:]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExtraSpaces = new(
        @"[ ]{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Raises a soft phrase toward a level the model reliably hears, and leaves a loud one alone.
    /// </summary>
    private static float[] LiftQuietSpeech(List<float> samples)
    {
        double sum = 0;
        foreach (var sample in samples) sum += (double)sample * sample;

        var rms = Math.Sqrt(sum / samples.Count);
        if (rms < 1e-8) return samples.ToArray();

        var gain = Math.Min(MaxQuietGain, TargetSpeechRms / rms);
        if (gain <= 1) return samples.ToArray();

        var lifted = new float[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            lifted[i] = Math.Clamp(samples[i] * (float)gain, -1f, 1f);
        }

        return lifted;
    }

    private static void Remember(Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Murmur");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "last-error.txt"), ex.ToString());
        }
        catch (IOException)
        {
            // The recording already failed. Losing the note must not replace that error.
        }
    }

    private void SetState(DictationState state)
    {
        State = state;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _hotkey.Pressed -= OnPressed;
        _hotkey.Dispose();

        if (_recording is not null)
        {
            await _recording.CancelAsync().ConfigureAwait(false);
            _recording.Dispose();
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
        await _transcriber.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
