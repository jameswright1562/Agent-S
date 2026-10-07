using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Windows.Automation;
using AgentS.Core;
using AgentS.Windows;

if (args.Length != 1) throw new ArgumentException("Pass the path to the built AgentS.Maui.exe.");
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var state = Path.Combine(AppContext.BaseDirectory, "ui-smoke-state");
Directory.CreateDirectory(state);
var token = Guid.NewGuid().ToString("N");
var info = new ProcessStartInfo(Path.GetFullPath(args[0])) { UseShellExecute = false };
info.Environment["AGENT_S_DOTNET_STATE"] = state;
info.Environment["AGENT_S_DOTNET_TOKEN"] = token;
info.Environment["AGENT_S_DOTNET_URL"] = $"http://127.0.0.1:{port}";
using var process = Process.Start(info) ?? throw new InvalidOperationException("App did not start.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
try
{
    AutomationElement? window = null;
    while (!timeout.IsCancellationRequested)
    {
        if (process.HasExited) throw new InvalidOperationException($"MAUI app exited with code {process.ExitCode}.");
        window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
            new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id));
        if (window is not null && window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "RunTaskButton")) is not null) break;
        await Task.Delay(300, timeout.Token);
    }
    if (window is null) throw new InvalidOperationException("No native MAUI window.");
    foreach (var id in new[] { "TaskInstruction", "ProfilePicker", "RunTaskButton", "WorkspaceStatus" })
        if (window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)) is null)
            throw new InvalidOperationException($"Missing native UI control: {id}");
    while (true)
    {
        try { (await http.GetAsync("api/status", timeout.Token)).EnsureSuccessStatusCode(); break; }
        catch (HttpRequestException) { await Task.Delay(200, timeout.Token); }
    }
    if (window.TryGetCurrentPattern(WindowPattern.Pattern, out var pattern))
        ((WindowPattern)pattern).SetWindowVisualState(WindowVisualState.Normal);
    window.SetFocus();
    await Task.Delay(1000, timeout.Token);
    var desktop = new WindowsDesktop();
    using var lease = desktop.AcquireLease();
    var editor = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "TaskInstruction"));
    var editorBounds = editor.Current.BoundingRectangle;
    var centerX = (int)(editorBounds.Left + editorBounds.Width / 2);
    var centerY = (int)(editorBounds.Top + editorBounds.Height / 2);
    var monitor = desktop.GetMonitors().Single(item => centerX >= item.Left && centerX < item.Left + item.Width && centerY >= item.Top && centerY < item.Top + item.Height);
    var screenshot = await desktop.CaptureAsync(monitor.Id, timeout.Token);
    if (screenshot.ForegroundWindow != window.Current.NativeWindowHandle)
        throw new InvalidOperationException("The smoke-test workspace is not the foreground target; refusing to send test input.");
    await desktop.ExecuteAsync(new(new() { Kind = ActionKind.Type, Text = "Native input verified · Ω", Overwrite = true },
        Point: new(centerX - monitor.Left, centerY - monitor.Top)), screenshot, timeout.Token);
    await Task.Delay(250, timeout.Token);
    if (!editor.TryGetCurrentPattern(ValuePattern.Pattern, out var value) || ((ValuePattern)value).Current.Value != "Native input verified · Ω")
        throw new InvalidOperationException("Native Unicode input did not reach the isolated workspace editor.");
    var output = Path.Combine(AppContext.BaseDirectory, "maui-workspace.png");
    var bounds = window.Current.BoundingRectangle;
    if (bounds.Width <= 0 || bounds.Height <= 0) throw new InvalidOperationException("The native window has no visible bounds.");
    using (var bitmap = new System.Drawing.Bitmap((int)bounds.Width, (int)bounds.Height))
    {
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.CopyFromScreen((int)bounds.Left, (int)bounds.Top, 0, 0, bitmap.Size);
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
    }
    if (screenshot.Png.Length == 0) throw new InvalidOperationException("Native desktop capture returned no PNG.");
    Console.WriteLine($"PASS: native MAUI window, workspace controls, authenticated backend, Unicode SendInput to the isolated editor, and screenshot. Image: {output}");
    (await http.PostAsync("api/shutdown", null, timeout.Token)).EnsureSuccessStatusCode();
}
finally
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
}
