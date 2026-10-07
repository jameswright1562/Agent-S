using System.Text.Json;
using AgentS.Core;

public static class CompatibilityApi
{
    public static async Task<IResult> ChatAsync(JsonElement request, AgentTaskManager tasks, ProfileService profiles, CancellationToken cancellationToken)
    {
        if (request.TryGetProperty("stream", out var stream) && stream.GetBoolean())
            throw new ArgumentException("Use /api/tasks/{id}/events for streaming task events; this compatibility endpoint returns completed results.");
        var messages = request.GetProperty("messages");
        var latest = messages.EnumerateArray().Last(message => message.GetProperty("role").GetString() == "user");
        var content = latest.GetProperty("content");
        var text = content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Join('\n', content.EnumerateArray()
            .Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()));
        var result = await ExecuteAsync(text, tasks, profiles, cancellationToken);
        return Results.Json(new
        {
            id = "chatcmpl-" + result.Id.ToString("N"), @object = "chat.completion", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "agent-s3",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = Summary(result) }, finish_reason = "stop" } }
        });
    }

    public static async Task<IResult> ResponsesAsync(JsonElement request, AgentTaskManager tasks, ProfileService profiles, CancellationToken cancellationToken)
    {
        if (request.TryGetProperty("stream", out var stream) && stream.GetBoolean()) throw new ArgumentException("Use the task events endpoint for streaming.");
        var input = request.GetProperty("input");
        var text = input.ValueKind == JsonValueKind.String ? input.GetString()! : string.Join('\n', input.EnumerateArray()
            .Where(item => item.GetProperty("role").GetString() == "user").SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(part => part.GetProperty("type").GetString() == "input_text").Select(part => part.GetProperty("text").GetString()));
        var result = await ExecuteAsync(text, tasks, profiles, cancellationToken);
        return Results.Json(new
        {
            id = "resp-" + result.Id.ToString("N"), @object = "response", status = "completed", model = "agent-s3",
            output = new[] { new { id = "msg-" + result.Id.ToString("N"), type = "message", role = "assistant", status = "completed", content = new[] { new { type = "output_text", text = Summary(result) } } } }
        });
    }

    private static async Task<TaskSnapshot> ExecuteAsync(string instruction, AgentTaskManager tasks, ProfileService profiles, CancellationToken cancellationToken)
    {
        var task = tasks.Launch(instruction, (await profiles.LoadAsync(cancellationToken)).Active);
        try { return await tasks.WaitAsync(task.Id, cancellationToken); }
        catch (OperationCanceledException) { tasks.Stop(task.Id); throw; }
    }
    private static string Summary(TaskSnapshot task) => $"Task {task.Id}: {task.State} ({task.DoneReason}).\n{task.Steps.LastOrDefault()?.Analysis}\n{task.Error}".Trim();
}
