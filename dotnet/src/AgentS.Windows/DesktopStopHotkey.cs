using System.Runtime.InteropServices;

namespace AgentS.Windows;

/// <summary>A global Ctrl+Alt+Escape stop signal on its own native message-loop thread.</summary>
public sealed class DesktopStopHotkey : IDisposable
{
    private const int Id = 0x531;
    private readonly Thread thread;
    private readonly Action stop;
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint threadId;
    private int disposed;
    public bool IsAvailable { get; }

    public DesktopStopHotkey(Action stop)
    {
        this.stop = stop;
        thread = new Thread(Run) { IsBackground = true, Name = "Agent S emergency stop" };
        thread.Start();
        IsAvailable = ready.Task.GetAwaiter().GetResult();
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        // PeekMessage creates the thread's message queue before shutdown can post WM_QUIT.
        PeekMessage(out _, 0, 0, 0, 0);
        var registered = RegisterHotKey(0, Id, 0x4003, 0x1B); // NOREPEAT | ALT | CONTROL, Escape.
        ready.TrySetResult(registered);
        if (!registered) return;
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
                if (message.Message == 0x0312 && message.WParam == (nuint)Id) stop();
        }
        finally { UnregisterHotKey(0, Id); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (IsAvailable) PostThreadMessage(threadId, 0x0012, 0, 0);
        thread.Join(TimeSpan.FromSeconds(2));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
