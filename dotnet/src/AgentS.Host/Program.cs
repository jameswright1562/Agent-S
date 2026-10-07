using AgentS.Core;
using AgentS.Infrastructure;
using AgentS.Windows;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddDebug();
var address = Environment.GetEnvironmentVariable("AGENT_S_DOTNET_URL") ?? "http://127.0.0.1:8765";
if (!Uri.TryCreate(address, UriKind.Absolute, out var url) || !url.IsLoopback || url.Scheme != "http" || url.AbsolutePath != "/")
    throw new ArgumentException("AGENT_S_DOTNET_URL must be a loopback HTTP address with no path.");
builder.WebHost.UseUrls(address);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase, false));
    options.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.RespectNullableAnnotations = true;
});
builder.Services.AddSingleton<IDesktop, WindowsDesktop>();
builder.Services.AddSingleton<ICodeExecutor, PowerShellExecutor>();
builder.Services.AddSingleton<ISecretProtector, WindowsSecretProtector>();
builder.Services.AddSingleton<IProfileStore>(services => new JsonProfileStore(Path.Combine(StatePaths.DirectoryPath, "profiles.json"), services.GetRequiredService<ISecretProtector>()));
builder.Services.AddSingleton<ITaskArchive>(new JsonTaskArchive(Path.Combine(StatePaths.DirectoryPath, "tasks")));
builder.Services.AddHttpClient<HttpLmmProvider>(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) });
builder.Services.AddSingleton<CodexProvider>();
builder.Services.AddSingleton<ILmmProvider, ProviderRouter>();
builder.Services.AddSingleton<AgentTaskManager>();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<LoginCoordinator>();
builder.Services.AddSingleton<DesktopSafetyService>();
builder.Services.AddHostedService(services => services.GetRequiredService<DesktopSafetyService>());
var app = builder.Build();
var token = await BackendToken.LoadAsync();
app.Use(async (context, next) =>
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (!BackendToken.Matches(token, authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : ""))
    {
        context.Response.StatusCode = 401;
        return;
    }
    try { await next(context); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException or KeyNotFoundException or System.Text.Json.JsonException or HttpRequestException)
    {
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = error switch { TaskConflictException => 409, KeyNotFoundException => 404, HttpRequestException => 502, _ => 422 };
        await context.Response.WriteAsJsonAsync(new { error = error.Message }, context.RequestAborted);
    }
});
app.MapGet("/api/status", (AgentTaskManager tasks, IDesktop desktop, DesktopSafetyService safety) => new { product = "AgentS.NET", version = "1.0.0", platform = "windows", activeTasks = tasks.List().Count(task => task.IsActive), monitors = desktop.GetMonitors(), emergencyStopAvailable = safety.IsAvailable });
app.MapPost("/api/shutdown", (IHostApplicationLifetime lifetime) => { lifetime.StopApplication(); return Results.Accepted(); });
app.MapGet("/api/providers", () => ProviderCatalog.Presets);
app.MapGet("/api/profiles", async (ProfileService profiles, CancellationToken ct) => ProfileService.Public(await profiles.LoadAsync(ct)));
app.MapPut("/api/profiles", async (ProfileUpdate request, ProfileService profiles, CancellationToken ct) => ProfileService.Public(await profiles.UpdateAsync(request, ct)));
app.MapGet("/api/tasks", (AgentTaskManager tasks) => tasks.List());
app.MapPost("/api/tasks", async (LaunchRequest request, ProfileService profiles, AgentTaskManager tasks, CancellationToken ct) =>
{
    var collection = await profiles.LoadAsync(ct);
    var profile = request.ProfileId is { } id ? collection.Profiles.Single(item => item.Id == id) : collection.Active;
    if (request.Approval is { } approval) profile = profile with { Settings = profile.Settings with { Approval = approval } };
    if (request.MaxSteps is { } max) profile = profile with { Settings = profile.Settings with { MaxSteps = max } };
    return Results.Json(tasks.Launch(request.Instruction, profile), statusCode: 202);
});
app.MapGet("/api/tasks/{id:guid}", (Guid id, AgentTaskManager tasks) => tasks.Get(id));
app.MapPost("/api/tasks/{id:guid}/stop", (Guid id, AgentTaskManager tasks) => { tasks.Stop(id); return Results.Ok(); });
app.MapPost("/api/tasks/{id:guid}/approve", (Guid id, ApprovalRequest request, AgentTaskManager tasks) => { tasks.Approve(id, request.Step, request.Token, request.Approved); return Results.Ok(); });
app.MapGet("/api/tasks/{id:guid}/screenshot", (Guid id, AgentTaskManager tasks) => tasks.Get(id).Screenshot is { } image ? Results.File(image, "image/png") : Results.NotFound());
app.MapGet("/api/screenshot", async (int? monitor, IDesktop desktop, CancellationToken ct) => Results.File((await desktop.CaptureAsync(monitor ?? 1, ct)).Png, "image/png"));
app.MapGet("/api/tasks/{id:guid}/events", async (Guid id, AgentTaskManager tasks, HttpContext context) =>
{
    context.Response.ContentType = "text/event-stream";
    TaskSnapshot? previous = null;
    while (!context.RequestAborted.IsCancellationRequested)
    {
        var current = tasks.Get(id);
        if (!ReferenceEquals(current, previous))
        {
            await context.Response.WriteAsync("data: " + System.Text.Json.JsonSerializer.Serialize(current, new System.Text.Json.JsonSerializerOptions(AgentJson.Options) { WriteIndented = false }) + "\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            previous = current;
        }
        if (!current.IsActive) break;
        await Task.Delay(250, context.RequestAborted);
    }
});
app.MapPost("/api/models", async (ConnectionRequest request, ProfileService profiles, ILmmProvider provider, CancellationToken ct) =>
{
    var settings = await profiles.ResolveProviderAsync(request, ct);
    return new { models = await provider.ListModelsAsync(settings, ct) };
});
app.MapPost("/api/connection/test", async (ConnectionRequest request, ProfileService profiles, ILmmProvider provider, CancellationToken ct) =>
{
    // Connection testing is discovery only: no billable generation occurs implicitly.
    var settings = await profiles.ResolveProviderAsync(request, ct);
    return new { ok = true, models = await provider.ListModelsAsync(settings, ct) };
});
app.MapGet("/api/codex/status", async (ProfileService profiles, CodexProvider provider, CancellationToken ct) => provider is not null ? await provider.AccountAsync((await profiles.LoadAsync(ct)).Active.Settings.Planner, ct) : null);
app.MapPost("/api/codex/login", async (ProfileService profiles, LoginCoordinator login, CancellationToken ct) => { login.Start((await profiles.LoadAsync(ct)).Active.Settings.Planner); return Results.Accepted(); });
app.MapPost("/api/codex/logout", async (ProfileService profiles, CodexProvider provider, CancellationToken ct) => { await provider.LogoutAsync((await profiles.LoadAsync(ct)).Active.Settings.Planner, ct); return Results.Ok(); });
app.MapGet("/api/codex/login", (LoginCoordinator login) => new { status = login.Status, error = login.Error });
app.MapGet("/v1/models", () => new { @object = "list", data = new[] { new { id = "agent-s3", @object = "model", owned_by = "agent-s" } } });
app.MapPost("/v1/chat/completions", CompatibilityApi.ChatAsync);
app.MapPost("/v1/responses", CompatibilityApi.ResponsesAsync);
await app.Services.GetRequiredService<AgentTaskManager>().RestoreHistoryAsync();
await app.RunAsync();

public partial class Program;
