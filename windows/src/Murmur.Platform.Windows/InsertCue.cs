using System.Runtime.InteropServices;
using NAudio.Wave;

namespace Murmur.Platform.Windows;

/// <summary>Short ticks played after text, a line break, or a line gap has actually been inserted.</summary>
internal static class InsertCue
{
    // Pinned so the playback stream can read the buffer for the whole cue.
    private static readonly byte[] TextWave = BuildTextWave();
    private static readonly GCHandle TextPin = GCHandle.Alloc(TextWave, GCHandleType.Pinned);
    private static readonly byte[] LineBreakWave = BuildLineBreakWave();
    private static readonly GCHandle LineBreakPin = GCHandle.Alloc(LineBreakWave, GCHandleType.Pinned);
    private static readonly byte[] LineGapWave = BuildLineGapWave();
    private static readonly GCHandle LineGapPin = GCHandle.Alloc(LineGapWave, GCHandleType.Pinned);

    /// <summary>The single high tick used when text lands.</summary>
    public static void PlayText() => Play(TextWave, TextPin);

    /// <summary>One low knock, used when a line break lands.</summary>
    public static void PlayLineBreak() => Play(LineBreakWave, LineBreakPin);

    /// <summary>Two low knocks, used when a line gap lands.</summary>
    public static void PlayLineGap() => Play(LineGapWave, LineGapPin);

    private static void Play(byte[] wave, GCHandle pin)
    {
        if (!pin.IsAllocated) return;

        // PlaySound keeps a single slot, so a second cue was coming out as the first one again.
        using var stream = new MemoryStream(wave, writable: false);
        using var reader = new WaveFileReader(stream);
        using var output = new WaveOutEvent();
        output.Init(reader);
        output.Play();

        var started = Environment.TickCount64;
        while (output.PlaybackState == PlaybackState.Playing && Environment.TickCount64 - started < 1000)
        {
            Thread.Sleep(5);
        }
    }

    /// <summary>A few dozen milliseconds of a soft high tick.</summary>
    private static byte[] BuildTextWave()
    {
        const int rate = 22050;
        const int samples = 880;
        var data = NewWave(rate, samples);
        for (var i = 0; i < samples; i++)
        {
            var time = i / (double)rate;
            var envelope = Math.Min(1d, i / 30d) * Math.Exp(-time * 55d);
            WriteTone(data, i, Math.Sin(2d * Math.PI * 1760d * time) * envelope * 0.16d);
        }

        return data;
    }

    /// <summary>One quiet low knock.</summary>
    private static byte[] BuildLineBreakWave()
    {
        const int rate = 22050;
        const int knock = 700;
        var data = NewWave(rate, knock);
        WriteKnock(data, 0, knock, rate);
        return data;
    }

    /// <summary>Two quiet low knocks, played as one cue.</summary>
    private static byte[] BuildLineGapWave()
    {
        const int rate = 22050;
        const int knock = 700;
        const int gap = 2600;
        var data = NewWave(rate, (knock * 2) + gap);
        WriteKnock(data, 0, knock, rate);
        WriteKnock(data, knock + gap, knock, rate);
        return data;
    }

    private static void WriteKnock(byte[] data, int start, int length, int rate)
    {
        for (var i = 0; i < length; i++)
        {
            var time = i / (double)rate;
            var envelope = Math.Min(1d, i / 20d) * Math.Exp(-time * 28d);
            WriteTone(data, start + i, Math.Sin(2d * Math.PI * 392d * time) * envelope * 0.2d);
        }
    }

    private static byte[] NewWave(int rate, int samples)
    {
        var data = new byte[44 + (samples * 2)];
        WriteAscii(data, 0, "RIFF");
        WriteInt(data, 4, 36 + (samples * 2));
        WriteAscii(data, 8, "WAVE");
        WriteAscii(data, 12, "fmt ");
        WriteInt(data, 16, 16);
        WriteShort(data, 20, 1);
        WriteShort(data, 22, 1);
        WriteInt(data, 24, rate);
        WriteInt(data, 28, rate * 2);
        WriteShort(data, 32, 2);
        WriteShort(data, 34, 16);
        WriteAscii(data, 36, "data");
        WriteInt(data, 40, samples * 2);
        return data;
    }

    private static void WriteTone(byte[] data, int index, double sample) =>
        WriteShort(data, 44 + (index * 2), (short)(sample * short.MaxValue));

    private static void WriteAscii(byte[] data, int offset, string text)
    {
        for (var i = 0; i < text.Length; i++) data[offset + i] = (byte)text[i];
    }

    private static void WriteInt(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteShort(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
    }
}
