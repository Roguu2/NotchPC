using System.Runtime.InteropServices;

namespace NotchPC.Platform.Shell;

/// <summary>
/// Mouse and keyboard from the phone (SendInput). Windows does not deliver it to apps running as administrator
/// (UIPI) or to the UAC prompt, and some anti-cheat games ignore injected input.
/// </summary>
public static partial class RemoteInput
{
    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;

    private const uint MouseMove = 0x0001;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;
    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint Wheel = 0x0800;
    private const uint HWheel = 0x1000;

    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const uint ExtendedKey = 0x0001;

    public const ushort VkMediaNext = 0xB0;
    public const ushort VkMediaPrev = 0xB1;
    public const ushort VkMediaPlayPause = 0xB3;
    public const ushort VkVolumeMute = 0xAD;
    public const ushort VkVolumeDown = 0xAE;
    public const ushort VkVolumeUp = 0xAF;

    public static void Move(int dx, int dy)
    {
        if (dx != 0 || dy != 0)
        {
            Send([MouseEvent(MouseMove, dx, dy, 0)]);
        }
    }

    /// <summary>Wheel in notches × 120 (positive = up / right).</summary>
    public static void Scroll(int vertical, int horizontal)
    {
        var list = new List<Input>(2);
        if (vertical != 0)
        {
            list.Add(MouseEvent(Wheel, 0, 0, vertical));
        }

        if (horizontal != 0)
        {
            list.Add(MouseEvent(HWheel, 0, 0, horizontal));
        }

        if (list.Count > 0)
        {
            Send([.. list]);
        }
    }

    public static void Button(string button, bool down)
    {
        var (d, u) = Flags(button);
        Send([MouseEvent(down ? d : u, 0, 0, 0)]);
    }

    public static void Click(string button, bool twice)
    {
        var (d, u) = Flags(button);
        var one = new[] { MouseEvent(d, 0, 0, 0), MouseEvent(u, 0, 0, 0) };
        Send(twice ? [.. one, .. one] : one);
    }

    /// <summary>Types text as Unicode characters (any language, emoji included), independent of the keyboard layout.</summary>
    public static void Type(string text)
    {
        var list = new List<Input>(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '\n')
            {
                list.Add(KeyEvent(0x0D, 0, 0));
                list.Add(KeyEvent(0x0D, 0, KeyUp));
                continue;
            }

            list.Add(KeyEvent(0, c, Unicode));
            list.Add(KeyEvent(0, c, Unicode | KeyUp));
        }

        if (list.Count > 0)
        {
            Send([.. list]);
        }
    }

    /// <summary>Presses the keys together (in order) and releases them in reverse, e.g. Ctrl, C.</summary>
    public static void Keys(IReadOnlyList<ushort> keys, int repeat = 1)
    {
        var list = new List<Input>(keys.Count * 2 * repeat);
        for (var r = 0; r < repeat; r++)
        {
            foreach (var k in keys)
            {
                list.Add(KeyEvent(k, 0, IsExtended(k) ? ExtendedKey : 0));
            }

            for (var i = keys.Count - 1; i >= 0; i--)
            {
                list.Add(KeyEvent(keys[i], 0, KeyUp | (IsExtended(keys[i]) ? ExtendedKey : 0)));
            }
        }

        if (list.Count > 0)
        {
            Send([.. list]);
        }
    }

    // Arrows, Home/End, PgUp/PgDn, Delete and Win need the extended flag or they act as the numeric keypad keys.
    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2E or 0x5B;

    private static (uint Down, uint Up) Flags(string button) => button switch
    {
        "right" => (RightDown, RightUp),
        "middle" => (MiddleDown, MiddleUp),
        _ => (LeftDown, LeftUp),
    };

    private static void Send(Input[] inputs) => _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());

    private static Input MouseEvent(uint flags, int dx, int dy, int data) => new()
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, MouseData = unchecked((uint)data), Flags = flags } },
    };

    private static Input KeyEvent(ushort vk, ushort scan, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = vk, ScanCode = scan, Flags = flags } },
    };

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint SendInput(uint count, [In] Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
}

/// <summary>Lock, sleep, shutdown and restart (shutdown / restart with a delay that can be cancelled).</summary>
public static partial class PowerActions
{
    public static bool Lock() => SystemToggles.Lock();

    /// <summary>Sleep (not hibernate). Windows may refuse it, e.g. when a policy forbids sleeping.</summary>
    public static bool Sleep() => SetSuspendState(0, 0, 0) != 0;

    /// <summary>Windows shows its own "about to shut down" notice; <see cref="Cancel"/> stops it.</summary>
    public static bool Shutdown(int delaySeconds) => RunShutdown($"/s /t {Math.Max(0, delaySeconds)}");

    public static bool Restart(int delaySeconds) => RunShutdown($"/r /t {Math.Max(0, delaySeconds)}");

    public static bool Cancel() => RunShutdown("/a");

    private static bool RunShutdown(string arguments)
    {
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe");
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return p is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [LibraryImport("powrprof.dll")]
    private static partial byte SetSuspendState(byte hibernate, byte force, byte wakeupEventsDisabled);
}
