using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Murmur.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Murmur.Platform.Windows;

/// <summary>
/// Microphone capture through WASAPI, delivering 16 kHz mono float.
/// </summary>
/// <remarks>
/// <para>
/// The capture device is the user's default recording device (<c>Role.Console</c>), not the
/// default communications device. Windows keeps those separate, and a virtual communications
/// endpoint such as SteelSeries Sonar can stay selected while the headset the user just chose
/// is the console default — and the virtual one returns silence.
/// </para>
/// <para>
/// The stream is opened in the device's mix format and converted in managed code. Asking
/// WASAPI for 16 kHz directly does open, but on a real headset it came back about 70× quieter
/// than the same mic in its native format, which leaves the meter stuck and the model with
/// nothing it can transcribe.
/// </para>
/// </remarks>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private const int BufferMilliseconds = 50;

    private readonly string? _deviceId;
    private WasapiCapture? _capture;

    private Channel<float[]>? _channel;
    private WaveFormat? _sourceFormat;
    private bool _nativeTargetFormat;

    /// <summary>Captures from a specific device, or the default when null.</summary>
    /// <param name="deviceId">An <c>MMDevice.ID</c>, or null for the system default.</param>
    public WasapiAudioCapture(string? deviceId = null) => _deviceId = deviceId;

    /// <inheritdoc />
    public bool IsCapturing { get; private set; }

    /// <summary>
    /// True when the microphone appears to be muted at the OS level.
    /// </summary>
    /// <remarks>
    /// When "Let desktop apps access your microphone" is off, WASAPI does not fail — it
    /// returns a stream of digital silence. There is no documented way for an unpackaged app
    /// to query that setting, so exact-zero samples are the only available signal. Real
    /// microphones have a noise floor, so a run of *precisely* zero is a reliable tell.
    /// </remarks>
    public bool LooksLikeBlockedMicrophone { get; private set; }

    private int _consecutiveSilentChunks;

    /// <inheritdoc />
    public async IAsyncEnumerable<AudioChunk> CaptureAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        StartCapture();

        try
        {
            await foreach (var buffer in _channel!.Reader
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                yield return new AudioChunk(buffer);
            }
        }
        finally
        {
            StopCapture();
        }
    }

    private void StartCapture()
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = _deviceId is null
            ? DefaultCaptureDevice(enumerator)
            : enumerator.GetDevice(_deviceId);

        // Bounded and drop-oldest so a slow consumer can never block the capture thread.
        // Losing the oldest audio is bad; stalling the audio engine is worse.
        _channel = Channel.CreateBounded<float[]>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

        _consecutiveSilentChunks = 0;
        LooksLikeBlockedMicrophone = false;

        foreach (var attempt in Formats(device))
        {
            var capture = new WasapiCapture(device, useEventSync: true, BufferMilliseconds)
            {
                ShareMode = AudioClientShareMode.Shared,
            };

            try
            {
                capture.WaveFormat = attempt;
                capture.DataAvailable += OnDataAvailable;
                capture.RecordingStopped += OnRecordingStopped;
                capture.StartRecording();

                _nativeTargetFormat =
                    capture.WaveFormat.SampleRate == AudioChunk.SampleRate &&
                    capture.WaveFormat.Channels == 1 &&
                    capture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

                if (!_nativeTargetFormat) _sourceFormat = capture.WaveFormat;

                _capture = capture;
                IsCapturing = true;
                return;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or NotSupportedException)
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.Dispose();
            }
        }

        throw new InvalidOperationException("Could not open the microphone in any supported format.");
    }

    /// <summary>
    /// The recording device Windows sound settings calls the default.
    /// </summary>
    /// <remarks>
    /// Console first, then multimedia, then communications. Communications is a second
    /// default, and it is the one a virtual cable tends to keep after the user has pointed
    /// the real default at a headset.
    /// </remarks>
    private static MMDevice DefaultCaptureDevice(MMDeviceEnumerator enumerator)
    {
        foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
        {
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role);
            }
            catch (COMException)
            {
                // This role has no default. Try the next one.
            }
        }

        throw new InvalidOperationException("No capture device is available.");
    }

    /// <summary>Formats to try, best first.</summary>
    private static IEnumerable<WaveFormat> Formats(MMDevice device)
    {
        // Native mix format. Managed resampling keeps the amplitude the mic actually produced.
        yield return device.AudioClient.MixFormat;

        // Only if the mix format itself will not open.
        yield return WaveFormat.CreateIeeeFloatWaveFormat(AudioChunk.SampleRate, 1);
        yield return new WaveFormat(AudioChunk.SampleRate, 16, 1);
    }

    /// <summary>
    /// Runs on NAudio's dedicated capture thread — never the UI thread.
    /// </summary>
    /// <remarks>
    /// <b>The buffer is reused on every callback.</b> <c>WasapiCapture</c> allocates one array
    /// in <c>InitializeCaptureDevice</c> and hands out the same reference every time, and
    /// <c>WaveInEventArgs</c> stores it without cloning. Anything retained past the end of
    /// this method is overwritten in place by the next callback. Copy, always.
    /// </remarks>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;

        if (_nativeTargetFormat)
        {
            var count = e.BytesRecorded / sizeof(float);
            var owned = new float[count];
            MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, e.BytesRecorded)).CopyTo(owned);
            Publish(owned);
            return;
        }

        Publish(ToModelFormat(e.Buffer, e.BytesRecorded, _sourceFormat!));
    }

    /// <summary>
    /// Turns one captured buffer into 16 kHz mono float, using only the bytes just recorded.
    /// </summary>
    private static float[] ToModelFormat(byte[] buffer, int bytes, WaveFormat format)
    {
        var encoding = format.Encoding;
        if (format is WaveFormatExtensible extensible)
        {
            if (extensible.SubFormat == IeeeFloatSubType) encoding = WaveFormatEncoding.IeeeFloat;
            else if (extensible.SubFormat == PcmSubType) encoding = WaveFormatEncoding.Pcm;
        }

        var channels = Math.Max(1, format.Channels);
        int frames;
        var mono = encoding == WaveFormatEncoding.Pcm
            ? PcmToMono(buffer, bytes, channels, out frames)
            : FloatToMono(buffer, bytes, channels, out frames);

        if (frames == 0 || format.SampleRate == AudioChunk.SampleRate) return mono;

        var outFrames = (int)((long)frames * AudioChunk.SampleRate / format.SampleRate);
        if (outFrames <= 0) return [];

        var resampled = new float[outFrames];
        var step = (double)format.SampleRate / AudioChunk.SampleRate;
        for (var i = 0; i < outFrames; i++)
        {
            var position = i * step;
            var left = (int)position;
            var right = Math.Min(left + 1, frames - 1);
            var fraction = (float)(position - left);
            resampled[i] = (mono[left] * (1f - fraction)) + (mono[right] * fraction);
        }

        return resampled;
    }

    private static float[] FloatToMono(byte[] buffer, int bytes, int channels, out int frames)
    {
        frames = bytes / (sizeof(float) * channels);
        var mono = new float[frames];
        var samples = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, frames * channels * sizeof(float)));
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++) sum += samples[(frame * channels) + channel];
            mono[frame] = sum / channels;
        }

        return mono;
    }

    private static float[] PcmToMono(byte[] buffer, int bytes, int channels, out int frames)
    {
        frames = bytes / (sizeof(short) * channels);
        var mono = new float[frames];
        var samples = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, frames * channels * sizeof(short)));
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                sum += samples[(frame * channels) + channel] / 32768f;
            }

            mono[frame] = sum / channels;
        }

        return mono;
    }

    private static readonly Guid IeeeFloatSubType = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid PcmSubType = new("00000001-0000-0010-8000-00AA00389B71");

    private void Publish(float[] samples)
    {
        DetectBlockedMicrophone(samples);
        _channel?.Writer.TryWrite(samples);   // TryWrite never blocks
    }

    private void DetectBlockedMicrophone(float[] samples)
    {
        var allZero = true;
        foreach (var sample in samples)
        {
            if (sample != 0f) { allZero = false; break; }
        }

        // ~1.5s of exactly-zero samples. A live microphone always has a noise floor, so this
        // means the OS is feeding us silence rather than the room being quiet.
        _consecutiveSilentChunks = allZero ? _consecutiveSilentChunks + 1 : 0;
        if (_consecutiveSilentChunks > 1500 / BufferMilliseconds) LooksLikeBlockedMicrophone = true;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // A non-null exception here is usually AUDCLNT_E_DEVICE_INVALIDATED — the device was
        // unplugged or reconfigured mid-capture.
        _channel?.Writer.TryComplete(e.Exception);
        IsCapturing = false;
    }

    private void StopCapture()
    {
        if (_capture is null) return;

        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        try { _capture.StopRecording(); } catch (COMException) { /* already gone */ }
        _capture.Dispose();
        _capture = null;

        _channel?.Writer.TryComplete();
        IsCapturing = false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        StopCapture();
        return ValueTask.CompletedTask;
    }
}
