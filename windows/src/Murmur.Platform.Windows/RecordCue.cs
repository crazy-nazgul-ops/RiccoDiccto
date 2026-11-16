using System.Runtime.InteropServices;
using Murmur.Abstractions;
using NAudio.Wave;

namespace Murmur.Platform.Windows;

/// <summary>
/// Two short tones: a double tap when capture starts, and a lower note when the model is ready.
/// </summary>
public sealed class RecordCue : IRecordCue
{
    private static readonly byte[] RecordingWave = BuildRecordingWave();
    private static readonly GCHandle RecordingPin = GCHandle.Alloc(RecordingWave, GCHandleType.Pinned);
    private static readonly byte[] ReadyWave = BuildReadyWave();
    private static readonly GCHandle ReadyPin = GCHandle.Alloc(ReadyWave, GCHandleType.Pinned);

    /// <inheritdoc />
    public void PlayRecording() => Play(RecordingWave, RecordingPin);

    /// <inheritdoc />
    public void PlayModelReady() => Play(ReadyWave, ReadyPin);

    private static void Play(byte[] wave, GCHandle pin)
    {
        if (!pin.IsAllocated) return;

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

    /// <summary>Two short high taps. The deck engaging.</summary>
    private static byte[] BuildRecordingWave()
    {
        const int rate = 22050;
        const int tick = 640;
        const int gap = 700;
        var data = NewWave(rate, (tick * 2) + gap);
        WriteTick(data, 0, tick, rate, 1318d);
        WriteTick(data, tick + gap, tick, rate, 1568d);
        return data;
    }

    /// <summary>One lower, slightly longer note. Distinct from the armed taps.</summary>
    private static byte[] BuildReadyWave()
    {
        const int rate = 22050;
        const int samples = 3600;
        var data = NewWave(rate, samples);
        for (var i = 0; i < samples; i++)
        {
            var time = i / (double)rate;
            var envelope = Math.Min(1d, i / 40d) * Math.Exp(-time * 14d);
            WriteTone(data, i, Math.Sin(2d * Math.PI * 494d * time) * envelope * 0.18d);
        }

        return data;
    }

    private static void WriteTick(byte[] data, int start, int length, int rate, double frequency)
    {
        for (var i = 0; i < length; i++)
        {
            var time = i / (double)rate;
            var envelope = Math.Min(1d, i / 16d) * Math.Exp(-time * 48d);
            WriteTone(data, start + i, Math.Sin(2d * Math.PI * frequency * time) * envelope * 0.2d);
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
