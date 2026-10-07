using System.Text.Json;

// Deterministic app-server protocol double: never contacts a model or controls the desktop.
while (await Console.In.ReadLineAsync() is { } line)
{
    using var request = JsonDocument.Parse(line);
    var root = request.RootElement;
    if (!root.TryGetProperty("method", out var method)) continue;
    if (!root.TryGetProperty("id", out var id)) continue;
    var parameters = root.TryGetProperty("params", out var p) ? p : default;
    object result = method.GetString() switch
    {
        "initialize" => new { capabilities = new { } },
        "account/read" => new { account = new { type = "chatgpt", email = "fake@example.test" } },
        "model/list" => new { data = new[] { new { id = "gpt-5.6-luna" } } },
        "thread/start" => Thread(parameters),
        "turn/start" => new { turn = new { id = "fake-turn", status = "inProgress" } },
        "account/logout" => new { },
        _ => throw new InvalidOperationException("Unexpected SDK method: " + method.GetString())
    };
    if (method.GetString() == "initialize")
        await EmitAsync(new { id = id.Clone(), method = "test/serverRequest", @params = new { } });
    if (method.GetString() == "turn/start")
    {
        if (parameters.GetProperty("effort").GetString() != "low") throw new InvalidOperationException("Tests must use low reasoning effort.");
        await EmitAsync(new { method = "item/completed", @params = new { threadId = "fake-thread", turnId = "fake-turn", item = new { type = "agentMessage", phase = "final_answer", text = "fake final response" } } });
        await EmitAsync(new { method = "turn/completed", @params = new { threadId = "fake-thread", turn = new { id = "fake-turn", status = "completed" } } });
    }
    await EmitAsync(new { id = id.Clone(), result });
}

static object Thread(JsonElement parameters)
{
    if (parameters.GetProperty("sandbox").GetString() != "read-only" || parameters.GetProperty("approvalPolicy").GetString() != "never" || !parameters.GetProperty("ephemeral").GetBoolean())
        throw new InvalidOperationException("Planner threads must be ephemeral and read-only with approvals denied.");
    return new { thread = new { id = "fake-thread" } };
}
static async Task EmitAsync(object message)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(message));
    await Console.Out.FlushAsync();
}
