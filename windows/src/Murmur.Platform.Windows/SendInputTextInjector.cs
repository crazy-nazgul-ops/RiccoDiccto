using System.Runtime.InteropServices;
using System.Text;
using Murmur.Abstractions;

namespace Murmur.Platform.Windows;

/// <summary>
/// Types text into whatever application has focus.
/// </summary>
/// <remarks>
/// <para>
/// <b>UI Automation is not an option here.</b> On macOS the Accessibility API can write text
/// directly; Windows has no counterpart. <c>TextPattern</c> is documented as providing no
/// means to insert or modify text, and <c>ValuePattern</c> replaces an entire field rather
/// than inserting at the caret — and multi-line controls do not support it at all. Microsoft's
/// own sample code says plainly: "text input must be simulated."
/// </para>
/// <para>
/// So <c>SendInput</c> is the primary path, not a fallback. Short text is typed as Unicode
/// packets, which leaves the clipboard untouched. Longer text goes via the clipboard, because
/// some terminals and Electron apps drop characters when thousands of synthetic keystrokes
/// arrive back to back.
/// </para>
/// </remarks>
public sealed class SendInputTextInjector : ITextInjector
{
    /// <summary>Above this many characters, paste instead of typing.</summary>
    private const int PasteThreshold = 200;

    /// <summary>Characters per <c>SendInput</c> call when typing.</summary>
    private const int ChunkSize = 40;

    /// <summary>Pause between chunks, so slower targets keep up.</summary>
    private static readonly TimeSpan ChunkGap = TimeSpan.FromMilliseconds(4);

    /// <summary>
    /// Time for the target to observe the new clipboard before Ctrl+V arrives. Without it, a
    /// fast paste can pick up the <i>previous</i> contents.
    /// </summary>
    private static readonly TimeSpan ClipboardSettle = TimeSpan.FromMilliseconds(60);

    /// <summary>Time for the target to finish reading before the clipboard is restored.</summary>
    private static readonly TimeSpan PasteSettle = TimeSpan.FromMilliseconds(500);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_V = 0x56;
    private const int VK_RETURN = 0x0D;

    private const uint MAPVK_VK_TO_VSC = 0;
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Message;
        public ushort ParamL, ParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, char[] className, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process, uint flags, char[] name, ref int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetPackageFullName(IntPtr process, ref int length, char[]? buffer);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr memory);

    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    /// <summary>Called to place text on the clipboard and paste it.</summary>
    /// <remarks>
    /// Injected rather than called directly because clipboard access is UI-framework specific
    /// and must happen on an STA thread — the app supplies an implementation that marshals to
    /// its own dispatcher.
    /// </remarks>
    public Func<string, CancellationToken, Task<bool>>? ClipboardPaste { get; init; }

    /// <inheritdoc />
    public async ValueTask<bool> InjectAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return true;

        // A newline is Enter. A vertical tab is a line gap: two Enters, one double knock.
        // Ctrl is lifted inside PressEnter so Word and Docs do not make a page break.
        bool inserted;
        var textLanded = false;
        var lineBreaks = 0;
        var lineGaps = 0;
        if (!text.Contains('\n', StringComparison.Ordinal) && !text.Contains('\v', StringComparison.Ordinal))
        {
            inserted = await InjectChunkAsync(text, cancellationToken).ConfigureAwait(false);
            textLanded = inserted;
        }
        else
        {
            inserted = true;
            var pending = new StringBuilder();
            async ValueTask FlushAsync()
            {
                if (pending.Length == 0) return;
                var chunk = await InjectChunkAsync(pending.ToString(), cancellationToken).ConfigureAwait(false);
                pending.Clear();
                inserted &= chunk;
                textLanded |= chunk;
            }

            foreach (var unit in text.Replace("\r\n", "\n", StringComparison.Ordinal))
            {
                if (unit == '\n')
                {
                    await FlushAsync().ConfigureAwait(false);
                    var broke = PressEnter();
                    inserted &= broke;
                    if (broke) lineBreaks++;
                    continue;
                }

                if (unit == '\v')
                {
                    await FlushAsync().ConfigureAwait(false);
                    var first = PressEnter();
                    var second = first && PressEnter();
                    inserted &= first && second;
                    if (second) lineGaps++;
                    continue;
                }

                pending.Append(unit);
            }

            await FlushAsync().ConfigureAwait(false);
        }

        if (textLanded) InsertCue.PlayText();
        for (var n = 0; n < lineBreaks; n++) InsertCue.PlayLineBreak();
        for (var n = 0; n < lineGaps; n++) InsertCue.PlayLineGap();
        return inserted;
    }

    private async ValueTask<bool> InjectChunkAsync(string text, CancellationToken cancellationToken)
    {
        // Newlines sent as Unicode packets do not reliably produce a new line — many controls
        // want a real VK_RETURN. Long text goes to the clipboard anyway, which handles them.
        // OneNote and the Store Notes apps drop Unicode packets entirely, so those get a paste
        // even when the transcript is a single short line.
        var hasNewlines = text.Contains('\n', StringComparison.Ordinal);
        var paste = text.Length > PasteThreshold || hasNewlines || ForegroundNeedsPaste();

        if (!paste) return TypeUnicode(text);

        if (ClipboardPaste is not null)
        {
            return await ClipboardPaste(text, cancellationToken).ConfigureAwait(false);
        }

        return PasteWithClipboard(text) || TypeWithNewlines(text);
    }

    /// <summary>
    /// True when the focused window will ignore Unicode key packets.
    /// </summary>
    /// <remarks>
    /// OneNote's page is a custom editor (<c>Framework::CFrame</c>) that never turns those
    /// packets into characters. Sticky Notes and other Store apps are hosted in
    /// <c>ApplicationFrameWindow</c> and do the same. A paste is the input both accept.
    /// </remarks>
    private static bool ForegroundNeedsPaste()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero) return false;

        var className = new char[256];
        var classLength = GetClassName(window, className, className.Length);
        if (classLength > 0 && new string(className, 0, classLength) is "ApplicationFrameWindow" or "Windows.UI.Core.CoreWindow" or "Framework::CFrame")
        {
            return true;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return false;

        try
        {
            var length = 0;
            var packaged = GetPackageFullName(process, ref length, null);
            if (packaged == ErrorInsufficientBuffer) return true;

            var image = new char[1024];
            var size = image.Length;
            if (!QueryFullProcessImageName(process, 0, image, ref size) || size <= 0) return false;

            var file = Path.GetFileName(new string(image, 0, size));
            return file.Equals("ONENOTE.EXE", StringComparison.OrdinalIgnoreCase)
                || file.Equals("ONENOTEM.EXE", StringComparison.OrdinalIgnoreCase)
                || file.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard and presses Ctrl+V.</summary>
    private static bool PasteWithClipboard(string text)
    {
        var previous = CaptureClipboard();
        if (!SetClipboardText(text)) return false;

        Thread.Sleep(ClipboardSettle);
        var pasted = PressCtrlV();
        // OneNote reads the clipboard after the keystroke has come back through its own
        // queue. Restoring any sooner pastes whatever was on the clipboard before.
        Thread.Sleep(TimeSpan.FromMilliseconds(1000));
        if (previous is not null) RestoreClipboard(previous);
        return pasted;
    }

    private static List<(uint Format, byte[] Bytes)>? CaptureClipboard()
    {
        if (!OpenClipboardForUse()) return null;

        try
        {
            var items = new List<(uint, byte[])>();
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                var data = GetClipboardData(format);
                if (data == IntPtr.Zero) continue;

                var size = GlobalSize(data);
                if (size == 0 || size > 8 * 1024 * 1024) continue;

                var pointer = GlobalLock(data);
                if (pointer == IntPtr.Zero) continue;

                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, (int)size);
                    items.Add((format, bytes));
                }
                finally
                {
                    _ = GlobalUnlock(data);
                }
            }

            return items;
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static bool SetClipboardText(string text)
    {
        if (!OpenClipboardForUse()) return false;

        try
        {
            if (!EmptyClipboard()) return false;

            var bytes = (text.Length + 1) * sizeof(char);
            var memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes);
            if (memory == IntPtr.Zero) return false;

            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero)
            {
                _ = GlobalFree(memory);
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                Marshal.WriteInt16(pointer, text.Length * sizeof(char), 0);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            if (SetClipboardData(CfUnicodeText, memory) != IntPtr.Zero) return true;

            _ = GlobalFree(memory);
            return false;
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static void RestoreClipboard(List<(uint Format, byte[] Bytes)> items)
    {
        if (!OpenClipboardForUse()) return;

        try
        {
            if (!EmptyClipboard()) return;

            foreach (var (format, bytes) in items)
            {
                var memory = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
                if (memory == IntPtr.Zero) continue;

                var pointer = GlobalLock(memory);
                if (pointer == IntPtr.Zero)
                {
                    _ = GlobalFree(memory);
                    continue;
                }

                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                }
                finally
                {
                    _ = GlobalUnlock(memory);
                }

                if (SetClipboardData(format, memory) == IntPtr.Zero) _ = GlobalFree(memory);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static bool OpenClipboardForUse()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(15);
        }

        return false;
    }

    /// <summary>Types arbitrary text as Unicode packets.</summary>
    /// <remarks>
    /// <para>
    /// Iterating by UTF-16 code unit is correct and intentional. A non-BMP character — an
    /// emoji, some CJK extensions — is two surrogate halves, and each is sent as its own
    /// event; Unicode-aware controls recombine them. The 16-bit <c>ScanCode</c> field cannot
    /// carry a full 21-bit code point.
    /// </para>
    /// <para>
    /// <b>Failure here is silent.</b> <c>SendInput</c> is subject to UIPI, and Microsoft
    /// documents that when it is blocked "neither GetLastError nor the return value will
    /// indicate the failure was caused by UIPI blocking." Injecting into an elevated window
    /// from a normal process can return success and do nothing.
    /// </para>
    /// </remarks>
    private static bool TypeUnicode(string text)
    {
        for (var offset = 0; offset < text.Length; offset += ChunkSize)
        {
            var length = Math.Min(ChunkSize, text.Length - offset);

            // Never split a surrogate pair across two SendInput calls.
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1])) length++;

            var inputs = new INPUT[length * 2];
            for (var i = 0; i < length; i++)
            {
                var unit = text[offset + i];
                inputs[i * 2] = UnicodeInput(unit, up: false);
                inputs[(i * 2) + 1] = UnicodeInput(unit, up: true);
            }

            if (SendInput((uint)inputs.Length, inputs, InputSize) != inputs.Length) return false;
            if (offset + length < text.Length) Thread.Sleep(ChunkGap);
        }

        return true;
    }

    /// <summary>Types text, sending a real Return for each newline.</summary>
    private static bool TypeWithNewlines(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && !TypeUnicode(lines[i])) return false;

            if (i < lines.Length - 1)
            {
                INPUT[] enter = [KeyInput(VK_RETURN, up: false), KeyInput(VK_RETURN, up: true)];
                if (SendInput(2, enter, InputSize) != 2) return false;
            }
        }

        return true;
    }

    /// <summary>Presses Ctrl+V, temporarily lifting any modifier the user is resting on.</summary>
    /// <remarks>
    /// Microsoft: "This function does not reset the keyboard's current state. Any keys that
    /// are already pressed when the function is called might interfere." A user resting on
    /// Shift would otherwise get Ctrl+Shift+V — "paste as plain text", or something else
    /// entirely depending on the app.
    /// </remarks>
    public static bool PressCtrlV()
    {
        var held = new List<int>();
        foreach (var key in new[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN })
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0) held.Add(key);
        }

        foreach (var key in held)
        {
            if (!Tap(KeyInput(key, up: true))) return false;
        }

        // Separate events, with a gap. OneNote drops a Ctrl+V that arrives as one packet.
        if (!Tap(KeyInput(VK_CONTROL, up: false))) return false;
        Thread.Sleep(30);
        if (!Tap(KeyInput(VK_V, up: false))) return false;
        Thread.Sleep(30);
        if (!Tap(KeyInput(VK_V, up: true))) return false;
        Thread.Sleep(30);
        if (!Tap(KeyInput(VK_CONTROL, up: true))) return false;

        foreach (var key in held)
        {
            if (!Tap(KeyInput(key, up: false))) return false;
        }

        return true;
    }

    /// <summary>Presses Enter, lifting Ctrl so Word and Docs do not turn it into a page break.</summary>
    private static bool PressEnter()
    {
        var held = new List<int>();
        foreach (var key in new[] { VK_SHIFT, VK_MENU, VK_LWIN, VK_RWIN, VK_CONTROL, VK_LCONTROL, VK_RCONTROL })
        {
            if ((GetAsyncKeyState(key) & 0x8000) != 0) held.Add(key);
        }

        foreach (var key in held)
        {
            if (!Tap(KeyInput(key, up: true))) return false;
        }

        if (!Tap(KeyInput(VK_RETURN, up: false))) return false;
        Thread.Sleep(30);
        if (!Tap(KeyInput(VK_RETURN, up: true))) return false;

        foreach (var key in held)
        {
            if (!Tap(KeyInput(key, up: false))) return false;
        }

        return true;
    }

    private static bool Tap(INPUT input) => SendInput(1, [input], InputSize) == 1;

    /// <summary>How long to wait after setting the clipboard before pasting.</summary>
    public static TimeSpan ClipboardSettleDelay => ClipboardSettle;

    /// <summary>How long to wait after pasting before restoring the clipboard.</summary>
    public static TimeSpan PasteSettleDelay => PasteSettle;

    private static INPUT UnicodeInput(ushort codeUnit, bool up) => new()
    {
        Type = INPUT_KEYBOARD,
        Union = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = 0,   // must be 0 for KEYEVENTF_UNICODE
                ScanCode = codeUnit,
                Flags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0),
                Time = 0,
                ExtraInfo = PushToTalkHook.InjectedTag,
            },
        },
    };

    private static INPUT KeyInput(int virtualKey, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (IsExtendedKey(virtualKey)) flags |= KEYEVENTF_EXTENDEDKEY;

        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            Union = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = (ushort)virtualKey,
                    ScanCode = (ushort)MapVirtualKey((uint)virtualKey, MAPVK_VK_TO_VSC),
                    Flags = flags,
                    Time = 0,
                    // Tagged so our own hook ignores it and doesn't re-trigger dictation.
                    ExtraInfo = PushToTalkHook.InjectedTag,
                },
            },
        };
    }

    private static bool IsExtendedKey(int virtualKey) => virtualKey is
        0xA3 or 0xA5 or 0x5B or 0x5C or   // right ctrl/alt, both Windows keys
        0x2D or 0x2E or 0x24 or 0x23 or   // insert, delete, home, end
        0x21 or 0x22 or                   // page up/down
        0x25 or 0x26 or 0x27 or 0x28;     // arrows
}
