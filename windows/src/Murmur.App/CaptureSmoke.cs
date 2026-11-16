namespace Murmur.App;

/// <summary>
/// Records a short stretch from the published executable and prints how much audio arrived.
/// </summary>
/// <remarks>
/// <c>--selftest</c> only constructs the capture type. The microphone library is loaded the
/// first time recording actually starts, which is exactly the step that can be missing from
/// a single-file publish and still leave the self-test green.
/// </remarks>
internal static class CaptureSmoke
{
    /// <summary>Captures for under a second.</summary>
    /// <returns>0 when samples arrived, 1 otherwise.</returns>
    public static int Run()
    {
        var capture = PlatformFactory.CreateAudioCapture();
        if (capture is null)
        {
            Console.WriteLine("capture: platform layer did not load");
            return 1;
        }

        try
        {
            return Record(capture).GetAwaiter().GetResult();
        }
        finally
        {
            capture.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task<int> Record(Murmur.Abstractions.IAudioCapture capture)
    {
        var samples = 0;
        var peak = 0f;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(800));

        try
        {
            await foreach (var chunk in capture.CaptureAsync(cts.Token).ConfigureAwait(false))
            {
                samples += chunk.Samples.Length;
                foreach (var sample in chunk.Samples.Span)
                {
                    var amplitude = Math.Abs(sample);
                    if (amplitude > peak) peak = amplitude;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The timeout ended the take.
        }

        Console.WriteLine($"capture: samples={samples} peak={peak:0.0000}");
        return samples > 0 ? 0 : 1;
    }
}
