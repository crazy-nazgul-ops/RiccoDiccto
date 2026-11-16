using Murmur.Abstractions;
using Murmur.Core;
using Murmur.Dictionary;
using Murmur.Testing;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Murmur.CoreTests;

/// <summary>
/// Exercises the entire dictation path with fakes.
/// </summary>
/// <remarks>
/// This is the substitute for a Windows machine. Everything here runs on any platform, so a
/// regression in the state machine, the chunking, or the correction pass is caught on the
/// developer's own machine rather than discovered by a user on Windows.
/// </remarks>
public sealed class DictationEngineTests
{
    private static DictationEngine Build(
        IAudioCapture capture,
        FakeHotkeySource hotkey,
        FakeTranscriber transcriber,
        RecordingTextInjector injector,
        params DictionaryEntry[] dictionary) =>
        new(capture, hotkey, transcriber, injector, () => dictionary, new FakeClock());

    private static DictationEngine Build(
        IAudioCapture capture,
        FakeHotkeySource hotkey,
        FakeTranscriber transcriber,
        RecordingTextInjector injector,
        IRecordCue recordCue,
        params DictionaryEntry[] dictionary) =>
        new(capture, hotkey, transcriber, injector, () => dictionary, new FakeClock(), recordCue: recordCue);

    /// <summary>Presses to start, waits for capture to drain, then presses again to stop.</summary>
    private static async Task DictateAsync(FakeHotkeySource hotkey, DictationEngine engine)
    {
        hotkey.Press();
        for (var i = 0; i < 2000 && engine.State != DictationState.Recording; i++) await Task.Yield();
        for (var i = 0; i < 20000 && engine.Level == 0; i++) await Task.Yield();

        hotkey.Press();
        for (var i = 0; i < 20000 && engine.State != DictationState.Idle; i++) await Task.Yield();
    }

    [Fact]
    public async Task Speech_is_transcribed_corrected_and_injected()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("I use cloud code every day");
        var injector = new RecordingTextInjector();

        await using var engine = Build(
            FakeAudioCapture.Tone(1.0), hotkey, transcriber, injector,
            DictionaryEntry.Correction("cloud code", "Claude Code"));

        DictationResult? completed = null;
        engine.Completed += (_, r) => completed = r;

        await DictateAsync(hotkey, engine);

        injector.Injected.ShouldHaveSingleItem();
        injector.Injected[0].ShouldBe("I use Claude Code every day");

        completed.ShouldNotBeNull();
        completed.Corrections.ShouldHaveSingleItem();
        completed.Corrections[0].To.ShouldBe("Claude Code");
    }

    [Fact]
    public async Task A_one_second_pause_transcribes_while_recording_continues()
    {
        var samples = new List<float>();
        samples.AddRange(ToneSamples(0.4));
        samples.AddRange(new float[AudioChunk.SampleRate + (AudioChunk.SampleRate / 5)]);
        samples.AddRange(ToneSamples(0.4));

        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(
            new FakeAudioCapture(samples.ToArray()),
            hotkey,
            new FakeTranscriber("first", "second"),
            injector);

        var first = new TaskCompletionSource<DictationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.Completed += (_, result) => first.TrySetResult(result);

        hotkey.Press();
        var finished = await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        finished.ShouldBe(first.Task);

        engine.State.ShouldBe(DictationState.Recording);
        injector.Injected.ShouldBe(["first "]);

        hotkey.Press();
        for (var i = 0; i < 20000 && engine.State != DictationState.Idle; i++) await Task.Yield();

        injector.Injected.Count.ShouldBe(2);
        injector.Injected[1].ShouldBe("second");
    }

    private static float[] ToneSamples(double seconds)
    {
        var count = (int)(seconds * AudioChunk.SampleRate);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / AudioChunk.SampleRate);
        }

        return samples;
    }

    [Fact]
    public async Task Saying_line_break_inserts_a_line_break_for_each_one()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(
            FakeAudioCapture.Tone(0.4), hotkey, new FakeTranscriber("hello line break line break there"), injector);

        await DictateAsync(hotkey, engine);

        injector.Injected.ShouldHaveSingleItem();
        injector.Injected[0].ShouldBe("hello\n\nthere");
    }

    [Fact]
    public async Task Saying_page_break_stays_words()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(
            FakeAudioCapture.Tone(0.4), hotkey, new FakeTranscriber("hello page break there"), injector);

        await DictateAsync(hotkey, engine);

        injector.Injected.ShouldHaveSingleItem();
        injector.Injected[0].ShouldBe("hello page break there");
    }

    [Fact]
    public async Task Saying_line_gap_inserts_a_gap_distinct_from_two_line_breaks()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(
            FakeAudioCapture.Tone(0.4), hotkey, new FakeTranscriber("hello line gap there"), injector);

        await DictateAsync(hotkey, engine);

        injector.Injected.ShouldHaveSingleItem();
        injector.Injected[0].ShouldBe("hello\vthere");
    }

    [Fact]
    public async Task Saying_click_stays_a_word()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(
            FakeAudioCapture.Tone(0.4), hotkey, new FakeTranscriber("hello click there"), injector);

        await DictateAsync(hotkey, engine);

        injector.Injected.ShouldHaveSingleItem();
        injector.Injected[0].ShouldBe("hello click there");
    }

    [Fact]
    public async Task Quiet_speech_is_still_transcribed()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();

        // Amplitude 0.008 is about −45 dBFS. The old gate treated that as silence.
        await using var engine = Build(
            FakeAudioCapture.Tone(0.4, 0.008f), hotkey, new FakeTranscriber("soft"), injector);

        await DictateAsync(hotkey, engine);

        injector.Injected.ShouldHaveSingleItem();
        injector.Injected[0].ShouldBe("soft");
    }

    [Fact]
    public async Task A_loaded_model_answers_with_both_sounds_when_recording_starts()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("hi");
        var cues = new CueLog();
        await transcriber.LoadAsync(CancellationToken.None);

        await using var engine = Build(
            FakeAudioCapture.Tone(0.4), hotkey, transcriber, new RecordingTextInjector(), cues);

        hotkey.Press();
        var played = await WaitForCues(cues, 2);

        played.ShouldBe(["recording", "ready"]);
        engine.State.ShouldBe(DictationState.Recording);
    }

    [Fact]
    public async Task The_ready_sound_waits_until_the_model_has_loaded()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hotkey = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("hi") { LoadDelay = gate.Task };
        var cues = new CueLog();

        await using var engine = Build(
            FakeAudioCapture.Tone(1.5), hotkey, transcriber, new RecordingTextInjector(), cues);

        hotkey.Press();
        var armed = await WaitForCues(cues, 1);
        armed.ShouldBe(["recording"]);

        gate.TrySetResult();
        var played = await WaitForCues(cues, 2);
        played.ShouldBe(["recording", "ready"]);
        engine.State.ShouldBe(DictationState.Recording);
    }

    [Fact]
    public async Task A_missing_model_plays_the_armed_sound_only()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("hi") { CanLoad = false };
        var cues = new CueLog();

        await using var engine = Build(
            FakeAudioCapture.Tone(0.4), hotkey, transcriber, new RecordingTextInjector(), cues);

        hotkey.Press();
        var armed = await WaitForCues(cues, 1);
        await Task.Delay(50);

        armed.ShouldBe(["recording"]);
        cues.Snapshot().ShouldBe(["recording"]);
        engine.State.ShouldBe(DictationState.Recording);
    }

    private static async Task<string[]> WaitForCues(CueLog cues, int count)
    {
        for (var i = 0; i < 20000 && cues.Snapshot().Length < count; i++) await Task.Yield();
        return cues.Snapshot();
    }

    [Fact]
    public async Task Silence_injects_nothing()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();

        // An engine that heard nothing returns empty — and empty must never be typed.
        await using var engine = Build(
            FakeAudioCapture.Silence(0.5), hotkey, new FakeTranscriber(""), injector);

        hotkey.Press();
        for (var i = 0; i < 2000 && engine.State != DictationState.Recording; i++) await Task.Yield();
        hotkey.Press();
        for (var i = 0; i < 20000 && engine.State != DictationState.Idle; i++) await Task.Yield();

        injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public async Task Release_without_press_is_ignored()
    {
        var hotkey = new FakeHotkeySource();
        var injector = new RecordingTextInjector();
        await using var engine = Build(
            FakeAudioCapture.Tone(0.2), hotkey, new FakeTranscriber("hello"), injector);

        hotkey.Release();
        for (var i = 0; i < 500; i++) await Task.Yield();

        engine.State.ShouldBe(DictationState.Idle);
        injector.Injected.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dictionary_terms_are_offered_to_the_engine_as_bias()
    {
        var hotkey = new FakeHotkeySource();
        var transcriber = new FakeTranscriber("anything");

        await using var engine = Build(
            FakeAudioCapture.Tone(0.4), hotkey, transcriber, new RecordingTextInjector(),
            DictionaryEntry.Term("Anthropic"),
            DictionaryEntry.Correction("cloud code", "Claude Code"));

        await DictateAsync(hotkey, engine);

        // Both the plain term and the *write* side of the correction get biased — the whole
        // point is to nudge the recogniser toward the correct spelling.
        transcriber.LastBias.ShouldContain("Anthropic");
        transcriber.LastBias.ShouldContain("Claude Code");
    }

    [Fact]
    public async Task State_returns_to_idle_after_a_dictation()
    {
        var hotkey = new FakeHotkeySource();
        await using var engine = Build(
            FakeAudioCapture.Tone(0.3), hotkey, new FakeTranscriber("done"), new RecordingTextInjector());

        engine.State.ShouldBe(DictationState.Idle);
        await DictateAsync(hotkey, engine);
        engine.State.ShouldBe(DictationState.Idle);
        engine.Level.ShouldBe(0);
    }

    /// <summary>
    /// The boundary is enforced by the compiler via CA1416, but this fails louder and names
    /// the reason: anything reachable from Core must run in CI on any platform.
    /// </summary>
    [Fact]
    public void Core_does_not_depend_on_any_platform_project()
    {
        var result = Types.InAssembly(typeof(DictationEngine).Assembly)
            .That().ResideInNamespace("Murmur.Core")
            .ShouldNot().HaveDependencyOn("Murmur.Platform")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            "Murmur.Core must stay platform-neutral: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }
}

/// <summary>Chunking behaviour around the encoder's hard limit.</summary>
public sealed class AudioSegmenterTests
{
    private static ReadOnlyMemory<float> Seconds(int n) =>
        new float[n * AudioChunk.SampleRate];

    [Fact]
    public void Short_audio_is_one_segment_and_is_not_copied()
    {
        var audio = Seconds(5);
        var pieces = AudioSegmenter.Split(audio);

        pieces.ShouldHaveSingleItem();
        pieces[0].Length.ShouldBe(audio.Length);
    }

    [Fact]
    public void Audio_at_the_limit_is_still_one_segment()
    {
        AudioSegmenter.Split(Seconds(AudioSegmenter.MaxSegmentSeconds)).ShouldHaveSingleItem();
    }

    [Fact]
    public void Long_audio_is_split_and_every_piece_is_under_the_limit()
    {
        var pieces = AudioSegmenter.Split(Seconds(200));

        pieces.Count.ShouldBeGreaterThan(1);
        foreach (var piece in pieces)
        {
            piece.Length.ShouldBeLessThanOrEqualTo(
                AudioSegmenter.MaxSegmentSeconds * AudioChunk.SampleRate);
            piece.Length.ShouldBeGreaterThan(0);
        }
    }

    [Fact]
    public void Splitting_loses_no_samples()
    {
        var pieces = AudioSegmenter.Split(Seconds(200));
        pieces.Sum(p => p.Length).ShouldBe(200 * AudioChunk.SampleRate);
    }

    /// <summary>
    /// 410 seconds is past the point where the encoder's position table overflows and
    /// inference throws rather than degrading. Nothing may reach it.
    /// </summary>
    [Fact]
    public void Nothing_ever_reaches_the_encoder_ceiling()
    {
        var pieces = AudioSegmenter.Split(Seconds(AudioSegmenter.EncoderCeilingSeconds + 100));

        foreach (var piece in pieces)
        {
            var seconds = (double)piece.Length / AudioChunk.SampleRate;
            seconds.ShouldBeLessThan(AudioSegmenter.EncoderCeilingSeconds);
        }
    }

    [Fact]
    public void A_cut_lands_in_the_quiet_part_rather_than_mid_word()
    {
        // Loud throughout, with one silent second placed inside the search window that
        // precedes the ideal cut point. The cut should be drawn to it.
        var total = AudioSegmenter.MaxSegmentSeconds * 2 * AudioChunk.SampleRate;
        var samples = new float[total];
        Array.Fill(samples, 0.5f);

        var quietStart = (AudioSegmenter.MaxSegmentSeconds - 3) * AudioChunk.SampleRate;
        Array.Clear(samples, quietStart, AudioChunk.SampleRate);

        var pieces = AudioSegmenter.Split(samples);
        var firstCut = pieces[0].Length;

        firstCut.ShouldBeGreaterThanOrEqualTo(quietStart);
        firstCut.ShouldBeLessThanOrEqualTo(quietStart + AudioChunk.SampleRate);
    }
}
