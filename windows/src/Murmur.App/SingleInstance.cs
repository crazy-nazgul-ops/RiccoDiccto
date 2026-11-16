namespace Murmur.App;

/// <summary>
/// Keeps a second launch from opening the microphone and the keyboard hook again.
/// </summary>
/// <remarks>
/// Each process transcribes on its own and types into whatever has focus. A second
/// process inserts the same utterance again, and the keystrokes land interleaved.
/// </remarks>
internal static class SingleInstance
{
    private const string MutexName = "Murmur.App.SingleInstance";
    private const string ShowEventName = "Murmur.App.Show";

    /// <summary>
    /// Takes the instance lock.
    /// </summary>
    /// <param name="mutex">The lock, when this process is the one that may run.</param>
    /// <returns>False when another instance already holds it.</returns>
    public static bool TryAcquire(out Mutex? mutex)
    {
        var created = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            // A crash leaves the mutex abandoned. WaitOne throws, and this thread owns it.
            if (created.WaitOne(0))
            {
                mutex = created;
                return true;
            }
        }
        catch (AbandonedMutexException)
        {
            mutex = created;
            return true;
        }

        created.Dispose();
        mutex = null;
        return false;
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static void SignalShow()
    {
        // Opening by name attaches to the event the running instance is waiting on.
        // Creating it here means that instance has not registered yet — an older build,
        // or a process that is still starting — so there is nobody to wake.
        using var shown = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out var created);
        if (!created) shown.Set();
    }

    /// <summary>
    /// Invokes <paramref name="show"/> when a later launch asks for the window.
    /// </summary>
    /// <param name="show">Marshals onto the UI thread itself.</param>
    public static void Listen(Action show)
    {
        var gate = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var thread = new Thread(() =>
        {
            while (gate.WaitOne()) show();
        })
        {
            IsBackground = true,
            Name = "Murmur instance signal",
        };
        thread.Start();
    }
}
