using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentS.Core;
using AgentS.Infrastructure;

namespace AgentS.Client;

public sealed record BackendStatus(string Product, string Version, string Platform, int ActiveTasks, IReadOnlyList<MonitorInfo> Monitors, bool EmergencyStopAvailable);
public sealed record ModelList(IReadOnlyList<string> Models);
public sealed record TaskLaunch(string Instruction, Guid? ProfileId = null, ApprovalMode? Approval = null, int? MaxSteps = null);
public sealed record ApprovalDecision(int Step, Guid Token, bool Approved);
public sealed record ProfileSave(ProfileCollection Collection, bool ClearPlannerKey = false, bool ClearGroundingKey = false);
public sealed record Connection(bool Grounding = false, ProviderSettings? Settings = null);
public sealed record LoginStatus(string Status, string? Error);

public sealed class BackendClient : IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim startup = new(1, 1);
    public BackendClient()
    {
        var address = Environment.GetEnvironmentVariable("AGENT_S_DOTNET_URL") ?? "http://127.0.0.1:8765";
        var uri = new Uri(address);
        if (!uri.IsLoopback || uri.Scheme != "http") throw new ArgumentException("The desktop backend must be a loopback HTTP address.");
        http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        await startup.WaitAsync(cancellationToken);
        try
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await BackendToken.LoadAsync(cancellationToken));
            try { _ = await StatusAsync(cancellationToken); return; }
            catch (HttpRequestException error) when (error.StatusCode is null) { }
            var path = Environment.GetEnvironmentVariable("AGENT_S_DOTNET_HOST") ?? Path.Combine(AppContext.BaseDirectory, "backend", "AgentS.Host.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("Backend executable is missing. Build the full solution or set AGENT_S_DOTNET_HOST.", path);
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException("Could not start the desktop backend.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            while (true)
            {
                if (process.HasExited) throw new InvalidOperationException($"Desktop backend exited with code {process.ExitCode}. Run AgentS.Host directly for diagnostics.");
                try { _ = await StatusAsync(timeout.Token); return; }
                catch (HttpRequestException error) when (error.StatusCode is null) { await Task.Delay(200, timeout.Token); }
            }
        }
        finally { startup.Release(); }
    }

    public Task<BackendStatus> StatusAsync(CancellationToken ct) => GetAsync<BackendStatus>("api/status", ct);
    public Task<ProfileCollection> ProfilesAsync(CancellationToken ct) => GetAsync<ProfileCollection>("api/profiles", ct);
    public Task<ProfileCollection> SaveProfilesAsync(ProfileSave profiles, CancellationToken ct) => SendAsync<ProfileCollection>(HttpMethod.Put, "api/profiles", profiles, ct);
    public Task<TaskSnapshot> LaunchAsync(TaskLaunch task, CancellationToken ct) => SendAsync<TaskSnapshot>(HttpMethod.Post, "api/tasks", task, ct);
    public Task<IReadOnlyList<TaskSnapshot>> TasksAsync(CancellationToken ct) => GetAsync<IReadOnlyList<TaskSnapshot>>("api/tasks", ct);
    public Task<TaskSnapshot> TaskAsync(Guid id, CancellationToken ct) => GetAsync<TaskSnapshot>($"api/tasks/{id}", ct);
    public Task StopAsync(Guid id, CancellationToken ct) => SendEmptyAsync($"api/tasks/{id}/stop", null, ct);
    public Task ApproveAsync(Guid id, ApprovalDecision decision, CancellationToken ct) => SendEmptyAsync($"api/tasks/{id}/approve", decision, ct);
    public Task ShutdownAsync(CancellationToken ct) => SendEmptyAsync("api/shutdown", null, ct);
    public Task<ModelList> ModelsAsync(Connection connection, CancellationToken ct) => SendAsync<ModelList>(HttpMethod.Post, "api/models", connection, ct);
    public Task<CodexAccount> CodexStatusAsync(CancellationToken ct) => GetAsync<CodexAccount>("api/codex/status", ct);
    public Task LoginAsync(CancellationToken ct) => SendEmptyAsync("api/codex/login", null, ct);
    public Task LogoutAsync(CancellationToken ct) => SendEmptyAsync("api/codex/logout", null, ct);
    public Task<LoginStatus> LoginStatusAsync(CancellationToken ct) => GetAsync<LoginStatus>("api/codex/login", ct);
    public Task<byte[]> ScreenshotAsync(int monitor, CancellationToken ct) => http.GetByteArrayAsync($"api/screenshot?monitor={monitor}", ct);
    public Task<byte[]> TaskScreenshotAsync(Guid id, CancellationToken ct) => http.GetByteArrayAsync($"api/tasks/{id}/screenshot", ct);

    public async Task<TaskSnapshot> WaitAsync(Guid id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var task = await TaskAsync(id, cancellationToken);
            if (!task.IsActive) return task;
            await Task.Delay(500, cancellationToken);
        }
    }

    private async Task<T> GetAsync<T>(string route, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(route, cancellationToken);
        await CheckAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(AgentJson.Options, cancellationToken) ?? throw new InvalidDataException("Backend returned an empty response.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string route, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, route) { Content = JsonContent.Create(body, options: AgentJson.Options) };
        using var response = await http.SendAsync(request, cancellationToken);
        await CheckAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(AgentJson.Options, cancellationToken) ?? throw new InvalidDataException("Backend returned an empty response.");
    }

    private async Task SendEmptyAsync(string route, object? body, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync(route, body, AgentJson.Options, cancellationToken);
        await CheckAsync(response, cancellationToken);
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"Backend HTTP {(int)response.StatusCode}: {content[..Math.Min(content.Length, 2048)]}", null, response.StatusCode);
    }

    public void Dispose() { http.Dispose(); startup.Dispose(); }
}
