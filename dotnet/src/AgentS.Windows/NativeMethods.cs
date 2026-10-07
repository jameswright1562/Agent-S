using System.Runtime.InteropServices;
using System.Text;

namespace AgentS.Windows;

internal static class NativeMethods
{
    internal delegate bool MonitorCallback(nint monitor, nint hdc, ref Rect rect, nint data);
    internal delegate bool WindowCallback(nint window, nint data);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorDetails
    {
        internal int Size;
        internal Rect Monitor, Work;
        internal uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] internal string Device;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MouseInput { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] internal struct KeyboardInput { internal ushort Key, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] internal struct InputUnion
    {
        [FieldOffset(0)] internal MouseInput Mouse;
        [FieldOffset(0)] internal KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint Type; internal InputUnion Value; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorDetails info);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint hdc);
    [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, uint operation);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(WindowCallback callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessDpiAwarenessContext(nint context);
}
