using System;
using System.Runtime.InteropServices;

namespace FineClipboard.Interop;

/// <summary>Win32 P/Invoke surface and a few composed helpers used by the app.</summary>
internal static class NativeMethods
{
    // ---- Window messages ----
    public const int WM_HOTKEY = 0x0312;
    public const int WM_CLIPBOARDUPDATE = 0x031D;

    // ---- Hotkey modifiers (RegisterHotKey) ----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- SendInput ----
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_CONTROL = 0x11;
    private const int VK_SHIFT = 0x10;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const ushort VK_V = 0x56;
    private const int SW_RESTORE = 9;

    /// <summary>Injects a Ctrl+V keystroke into the foreground window.</summary>
    public static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, up: false),
            Key(VK_V, up: false),
            Key(VK_V, up: true),
            Key(VK_CONTROL, up: true),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    /// <summary>True while a physical modifier from the triggering global shortcut is still held.</summary>
    public static bool AreShortcutModifiersPressed() =>
        IsKeyPressed(VK_CONTROL) || IsKeyPressed(VK_SHIFT) || IsKeyPressed(VK_MENU) ||
        IsKeyPressed(VK_LWIN) || IsKeyPressed(VK_RWIN);

    private static bool IsKeyPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = up ? KEYEVENTF_KEYUP : 0,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    /// <summary>
    /// Brings <paramref name="hWnd"/> to the foreground reliably by temporarily
    /// attaching input threads to bypass Windows' foreground-lock heuristics.
    /// </summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr fg = GetForegroundWindow();
        if (fg == hWnd)
        {
            return;
        }

        uint targetThread = GetWindowThreadProcessId(hWnd, out _);
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint cur = GetCurrentThreadId();

        bool attachedFg = fgThread != cur && AttachThreadInput(fgThread, cur, true);
        bool attachedTarget = targetThread != cur && targetThread != fgThread && AttachThreadInput(targetThread, cur, true);

        if (IsIconic(hWnd))
        {
            ShowWindow(hWnd, SW_RESTORE);
        }

        SetForegroundWindow(hWnd);
        SetActiveWindow(hWnd);

        if (attachedTarget)
        {
            AttachThreadInput(targetThread, cur, false);
        }
        if (attachedFg)
        {
            AttachThreadInput(fgThread, cur, false);
        }
    }

    // ---- Multi-monitor positioning (per-monitor DPI aware) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>
    /// Moves/sizes the window onto the monitor under the mouse cursor,
    /// just below the cursor, clamped to that monitor's work area. Sizes the window in
    /// physical pixels using the target monitor's DPI so it renders correctly on mixed-DPI setups.
    /// </summary>
    public static void PositionWindowAtCursor(IntPtr hwnd, double dipWidth, double dipHeight)
    {
        if (hwnd == IntPtr.Zero || !GetCursorPos(out POINT cursor))
        {
            return;
        }

        IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        uint dpiX = 96, dpiY = 96;
        try
        {
            GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY);
        }
        catch
        {
            dpiX = dpiY = 96;
        }
        if (dpiX == 0) dpiX = 96;
        if (dpiY == 0) dpiY = 96;

        int physW = (int)Math.Round(dipWidth * dpiX / 96.0);
        int physH = (int)Math.Round(dipHeight * dpiY / 96.0);
        int margin = (int)Math.Round(8 * dpiY / 96.0);

        RECT work = info.rcWork;
        int x = cursor.X - physW / 2;
        int y = cursor.Y + margin;
        x = Math.Clamp(x, work.left, Math.Max(work.left, work.right - physW));
        y = Math.Clamp(y, work.top, Math.Max(work.top, work.bottom - physH));

        SetWindowPos(hwnd, HWND_TOPMOST, x, y, physW, physH, SWP_SHOWWINDOW);
    }
}
