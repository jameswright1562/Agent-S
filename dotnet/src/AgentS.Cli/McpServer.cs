using System.Text.Json;
using AgentS.Client;
using AgentS.Core;

namespace AgentS.Cli;

public sealed class McpServer(BackendClient client)
{
    private static object Tool(string name, string description, object properties, string[]? required = null) => new
    {
        name, description, inputSchema = new { type = "object", properties, required = required ?? [], additionalProperties = false }
    };
    private static readonly object[] Tools = [
        Tool("server_status", "Backend status and monitor inventory", new { }),
        Tool("get_config", "Read profiles with API keys excluded", new { }),
        Tool("launch_task", "Launch one supervised desktop task", new { instruction = new { type = "string" }, approval = new { type = "string", @enum = new[] { "dryRun", "manual", "auto" } }, maxSteps = new { type = "integer", minimum = 1, maximum = 60 } }, ["instruction"]),
        Tool("task_status", "Read task state and pending approval", new { taskId = new { type = "string" } }, ["taskId"]),
        Tool("stop_task", "Cancel a desktop task", new { taskId = new { type = "string" } }, ["taskId"]),
        Tool("approve_step", "Approve/reject a specific pending action", new { taskId = new { type = "string" }, step = new { type = "integer" }, token = new { type = "string" }, approved = new { type = "boolean" } }, ["taskId", "step", "token", "approved"]),
        Tool("list_models", "Discover models without generation", new { grounding = new { type = "boolean" } }),
        Tool("get_screenshot", "Capture the selected monitor as PNG", new { monitor = new { type = "integer", minimum = 1, maximum = 32 } }),
        Tool("wait_task", "Poll a task for up to 30 seconds", new { taskId = new { type = "string" } }, ["taskId"]),
        Tool("update_config", "Replace the profile collection; blank API keys preserve existing secrets", new { collection = new { type = "object" } }, ["collection"])
    ];

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken) is { } line)
        {
            object? response;
            JsonElement id = default;
            try
            {
                if (line.Length > 1024 * 1024) throw new JsonException("Request exceeds limits.");
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement;
                if (!request.TryGetProperty("id", out id)) continue;
                id = id.Clone();
                var method = request.GetProperty("method").GetString();
                object result = method switch
                {
                    "initialize" => new { protocolVersion = "2025-11-25", capabilities = new { tools = new { listChanged = false } }, serverInfo = new { name = "agent-s-dotnet", version = "1.0.0" } },
                    "ping" => new { },
                    "tools/list" => new { tools = Tools },
                    "tools/call" => await CallAsync(request.GetProperty("params"), cancellationToken),
                    _ => throw new MissingMethodException("Unknown MCP method.")
                };
                response = new { jsonrpc = "2.0", id, result };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or HttpRequestException or IOException or MissingMethodException or KeyNotFoundException)
            {
                response = new { jsonrpc = "2.0", id = id.ValueKind == JsonValueKind.Undefined ? (object?)null : id,
                    error = new { code = error is JsonException ? -32700 : error is MissingMethodException ? -32601 : -32602, message = error.Message } };
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private async Task<object> CallAsync(JsonElement request, CancellationToken cancellationToken)
    {
        try
        {
            await client.EnsureStartedAsync(cancellationToken);
            var name = request.GetProperty("name").GetString();
            var args = request.TryGetProperty("arguments", out var arguments) ? arguments : JsonSerializer.SerializeToElement(new { });
            Guid Id() => Guid.Parse(args.GetProperty("taskId").GetString()!);
            object? result;
            switch (name)
            {
                case "server_status": result = await client.StatusAsync(cancellationToken); break;
                case "get_config": result = await client.ProfilesAsync(cancellationToken); break;
                case "launch_task":
                    result = await client.LaunchAsync(new(args.GetProperty("instruction").GetString()!,
                        Approval: args.TryGetProperty("approval", out var approval) ? Enum.Parse<ApprovalMode>(approval.GetString()!, true) : null,
                        MaxSteps: args.TryGetProperty("maxSteps", out var steps) ? steps.GetInt32() : null), cancellationToken); break;
                case "task_status": result = await client.TaskAsync(Id(), cancellationToken); break;
                case "stop_task": await client.StopAsync(Id(), cancellationToken); result = new { stopped = true }; break;
                case "approve_step": await client.ApproveAsync(Id(), new(args.GetProperty("step").GetInt32(), Guid.Parse(args.GetProperty("token").GetString()!), args.GetProperty("approved").GetBoolean()), cancellationToken); result = new { accepted = true }; break;
                case "list_models": result = await client.ModelsAsync(new(args.TryGetProperty("grounding", out var grounding) && grounding.GetBoolean()), cancellationToken); break;
                case "get_screenshot":
                    var image = await client.ScreenshotAsync(args.TryGetProperty("monitor", out var monitor) ? monitor.GetInt32() : 1, cancellationToken);
                    return new { content = new[] { new { type = "image", data = Convert.ToBase64String(image), mimeType = "image/png" } } };
                case "wait_task":
                    using (var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        wait.CancelAfter(TimeSpan.FromSeconds(30));
                        try { result = await client.WaitAsync(Id(), wait.Token); }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { result = await client.TaskAsync(Id(), cancellationToken); }
                    }
                    break;
                case "update_config":
                    var collection = args.GetProperty("collection").Deserialize<ProfileCollection>(AgentJson.Options) ?? throw new ArgumentException("Missing collection.");
                    result = await client.SaveProfilesAsync(new(collection), cancellationToken); break;
                default: throw new ArgumentException("Unknown tool.");
            }
            return new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result, AgentJson.Options) } } };
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or HttpRequestException or IOException)
        {
            return new { isError = true, content = new[] { new { type = "text", text = error.Message } } };
        }
    }
}
