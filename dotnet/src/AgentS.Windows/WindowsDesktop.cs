using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using AgentS.Core;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OcrWord = AgentS.Core.OcrWord;

namespace AgentS.Windows;

public sealed class WindowsDesktop : IDesktop
{
    public WindowsDesktop() => NativeMethods.SetProcessDpiAwarenessContext(new nint(-4));

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(0, 0, (nint handle, nint _, ref NativeMethods.Rect rect, nint _) =>
        {
            var details = new NativeMethods.MonitorDetails { Size = Marshal.SizeOf<NativeMethods.MonitorDetails>(), Device = "" };
            if (!NativeMethods.GetMonitorInfo(handle, ref details)) return true;
            monitors.Add(new(monitors.Count + 1, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, details.Device));
            return true;
        }, 0);
        return monitors;
    }

    public Task<Screenshot> CaptureAsync(int monitor, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var selected = GetMonitors().SingleOrDefault(item => item.Id == monitor) ?? throw new ArgumentException("Selected monitor is not available.");
        var foreground = NativeMethods.GetForegroundWindow();
        using var bitmap = new Bitmap(selected.Width, selected.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        var screen = NativeMethods.GetDC(0);
        if (screen == 0) throw new Win32Exception("Could not access the desktop.");
        var target = graphics.GetHdc();
        try
        {
            if (!NativeMethods.BitBlt(target, 0, 0, selected.Width, selected.Height, screen, selected.Left, selected.Top, 0x00CC0020 | 0x40000000))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { graphics.ReleaseHdc(target); NativeMethods.ReleaseDC(0, screen); }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return Task.FromResult(new Screenshot(output.ToArray(), selected, DateTimeOffset.UtcNow, foreground.ToInt64()));
    }

    public async Task<IReadOnlyList<OcrWord>> RecognizeTextAsync(Screenshot screenshot, CancellationToken cancellationToken)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("Install a Windows OCR language pack.");
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(screenshot.Png);
            await writer.StoreAsync().AsTask(cancellationToken);
            writer.DetachStream();
        }
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
        var scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { ScaledWidth = (uint)(decoder.PixelWidth * scale), ScaledHeight = (uint)(decoder.PixelHeight * scale) };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken);
        return result.Lines.SelectMany(line => line.Words).Select(word => new OcrWord(word.Text,
            (int)(word.BoundingRect.X / scale), (int)(word.BoundingRect.Y / scale),
            (int)(word.BoundingRect.Width / scale), (int)(word.BoundingRect.Height / scale))).ToArray();
    }

    public IDisposable AcquireLease()
    {
        var semaphore = new Semaphore(1, 1, "Local\\AgentS.NET.Desktop");
        if (!semaphore.WaitOne(0)) { semaphore.Dispose(); throw new TaskConflictException("Another .NET Agent S process owns the desktop."); }
        return new DesktopLease(semaphore);
    }

    private sealed class DesktopLease(Semaphore semaphore) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) { semaphore.Release(); semaphore.Dispose(); }
        }
    }

    public async Task ExecuteAsync(PreparedAction prepared, Screenshot observation, CancellationToken cancellationToken)
    {
        var action = prepared.Action;
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetMonitors().Contains(observation.Monitor)) throw new InvalidOperationException("Monitor layout changed; restart the task.");
        foreach (var key in action.Keys.Concat(action.HoldKeys)) _ = VirtualKey(key);
        if (action.Kind is not (ActionKind.Open or ActionKind.SwitchApplications or ActionKind.Wait or ActionKind.SaveToKnowledge))
        {
            var foreground = new nint(observation.ForegroundWindow);
            if (!NativeMethods.IsWindow(foreground)) throw new InvalidOperationException("The target window was closed.");
            NativeMethods.ShowWindow(foreground, 9);
            NativeMethods.SetForegroundWindow(foreground);
            await Task.Delay(150, cancellationToken);
            if (NativeMethods.GetForegroundWindow() != foreground) throw new InvalidOperationException("Windows refused to restore the target application's keyboard focus.");
        }
        var held = new List<string>();
        try
        {
            foreach (var key in action.HoldKeys) { Key(key, true); held.Add(key); }
            switch (action.Kind)
            {
                case ActionKind.Click:
                    Move(prepared.Point, observation.Monitor);
                    for (int click = 0; click < action.Clicks; click++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Click(action.Button);
                        if (click + 1 < action.Clicks) await Task.Delay(100, cancellationToken);
                    }
                    break;
                case ActionKind.Type:
                    if (prepared.Point is not null) { Move(prepared.Point, observation.Monitor); Click(MouseButton.Left); }
                    if (action.Overwrite) { Hotkey(["ctrl", "a"]); Key("backspace", true); Key("backspace", false); }
                    TypeText(action.Text, cancellationToken);
                    if (action.Enter) { Key("enter", true); Key("enter", false); }
                    break;
                case ActionKind.Scroll:
                    Move(prepared.Point, observation.Monitor);
                    Mouse(action.Horizontal ? 0x1000u : 0x0800u, unchecked((uint)(action.Clicks * 120)));
                    break;
                case ActionKind.DragAndDrop:
                case ActionKind.HighlightTextSpan:
                    Move(prepared.Start, observation.Monitor);
                    Mouse(ButtonFlag(action.Button, true));
                    try
                    {
                        var start = prepared.Start!.Value;
                        var end = prepared.End ?? throw new ArgumentException("Missing drag endpoint.");
                        for (int index = 1; index <= 30; index++)
                        {
                            await Task.Delay(33, cancellationToken);
                            Move(new ScreenPoint(start.X + (end.X - start.X) * index / 30, start.Y + (end.Y - start.Y) * index / 30), observation.Monitor);
                        }
                    }
                    finally { Mouse(ButtonFlag(action.Button, false)); }
                    break;
                case ActionKind.Hotkey: Hotkey(action.Keys); break;
                case ActionKind.HoldAndPress:
                    foreach (var key in action.Keys) { cancellationToken.ThrowIfCancellationRequested(); Key(key, true); Key(key, false); }
                    break;
                case ActionKind.Wait: await Task.Delay(TimeSpan.FromSeconds(action.Seconds), cancellationToken); break;
                case ActionKind.Open:
                    if (File.Exists(action.Target) || Directory.Exists(action.Target) || Uri.TryCreate(action.Target, UriKind.Absolute, out _))
                        Process.Start(new ProcessStartInfo(action.Target) { UseShellExecute = true });
                    else
                    {
                        // Match the source product's ability to open applications by display name.
                        Hotkey(["win"]);
                        await Task.Delay(400, cancellationToken);
                        TypeText(action.Target, cancellationToken);
                        await Task.Delay(900, cancellationToken);
                        Key("enter", true);
                        Key("enter", false);
                    }
                    break;
                case ActionKind.SwitchApplications: SwitchWindow(action.Target); break;
                default: throw new InvalidOperationException($"Action {action.Kind} is not a desktop input operation.");
            }
        }
        finally { foreach (var key in held.AsEnumerable().Reverse()) Key(key, false); }
    }

    private static void SwitchWindow(string title)
    {
        nint target = 0;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (!NativeMethods.IsWindowVisible(window)) return true;
            var text = new StringBuilder(1024);
            NativeMethods.GetWindowText(window, text, text.Capacity);
            if (!text.ToString().Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
            target = window;
            return false;
        }, 0);
        if (target == 0) throw new InvalidOperationException($"No open window matches '{title}'.");
        NativeMethods.ShowWindow(target, 9);
        if (!NativeMethods.SetForegroundWindow(target)) throw new InvalidOperationException("Could not switch to the selected window.");
    }

    private static void Move(ScreenPoint? point, MonitorInfo monitor)
    {
        if (point is not { } value || value.X < 0 || value.Y < 0 || value.X >= monitor.Width || value.Y >= monitor.Height)
            throw new ArgumentException("Input point is outside the selected monitor.");
        if (!NativeMethods.SetCursorPos(monitor.Left + value.X, monitor.Top + value.Y)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void Click(MouseButton button) { Mouse(ButtonFlag(button, true)); Mouse(ButtonFlag(button, false)); }
    private static void TypeText(string text, CancellationToken cancellationToken)
    {
        foreach (var character in text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send(new NativeMethods.Input { Type = 1, Value = new() { Keyboard = new() { Scan = character, Flags = 4 } } },
                 new NativeMethods.Input { Type = 1, Value = new() { Keyboard = new() { Scan = character, Flags = 6 } } });
        }
    }
    private static uint ButtonFlag(MouseButton button, bool down) => button switch
    {
        MouseButton.Left => down ? 0x0002u : 0x0004u,
        MouseButton.Right => down ? 0x0008u : 0x0010u,
        MouseButton.Middle => down ? 0x0020u : 0x0040u,
        _ => throw new ArgumentException("Unknown mouse button.")
    };
    private static void Mouse(uint flags, uint data = 0) => Send(new NativeMethods.Input { Value = new() { Mouse = new() { Flags = flags, Data = data } } });
    private static void Key(string key, bool down)
    {
        var code = VirtualKey(key);
        var extended = code is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0xA3 or 0xA5;
        Send(new NativeMethods.Input { Type = 1, Value = new() { Keyboard = new() { Key = code, Flags = (down ? 0u : 2u) | (extended ? 1u : 0u) } } });
    }
    private static void Hotkey(IEnumerable<string> keys)
    {
        var pressed = new List<string>();
        try { foreach (var key in keys) { Key(key, true); pressed.Add(key); } }
        finally { foreach (var key in pressed.AsEnumerable().Reverse()) Key(key, false); }
    }
    private static void Send(params NativeMethods.Input[] inputs)
    {
        if (NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected desktop input (the target may be elevated).");
    }
    private static ushort VirtualKey(string key)
    {
        var name = key.ToLowerInvariant();
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0])) return char.ToUpperInvariant(name[0]);
        if (name.StartsWith('f') && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 24) return (ushort)(0x70 + function - 1);
        return name switch
        {
            "ctrl" or "control" => 0x11, "shift" => 0x10, "alt" => 0x12, "win" or "super" => 0x5B,
            "ctrlleft" => 0xA2, "ctrlright" => 0xA3, "shiftleft" => 0xA0, "shiftright" => 0xA1,
            "altleft" => 0xA4, "altright" => 0xA5, "winleft" => 0x5B, "winright" => 0x5C,
            "enter" or "return" => 0x0D, "escape" or "esc" => 0x1B, "tab" => 0x09, "space" => 0x20,
            "backspace" => 0x08, "delete" => 0x2E, "insert" => 0x2D, "home" => 0x24, "end" => 0x23,
            "pageup" => 0x21, "pagedown" => 0x22, "left" => 0x25, "up" => 0x26, "right" => 0x27, "down" => 0x28,
            "capslock" => 0x14, "printscreen" => 0x2C, "pause" => 0x13, "numlock" => 0x90,
            _ => throw new ArgumentException($"Unsupported Windows key: {key}")
        };
    }
}
