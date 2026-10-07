using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentS.Core;

namespace AgentS.Infrastructure;

public sealed record CodexAccount(bool SignedIn, string Account, string? Error = null);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CodexTextInput), "text")]
[JsonDerivedType(typeof(CodexImageInput), "image")]
public abstract record CodexInput;
public sealed record CodexTextInput([property: JsonPropertyName("text")] string Text) : CodexInput;
public sealed record CodexImageInput([property: JsonPropertyName("url")] string Url) : CodexInput;

public sealed class CodexProvider : ILmmProvider
{
    public static (string? Instructions, IReadOnlyList<CodexInput> Input) ConvertMessages(IReadOnlyList<LmmMessage> messages)
    {
        var instructions = new List<string>();
        var inputs = new List<CodexInput>();
        foreach (var message in messages)
        {
            if (message.Role == MessageRole.System)
            {
                if (message.Content.Any(part => part is not TextContent)) throw new ArgumentException("System instructions must be text.");
                instructions.Add(message.TextValue);
                continue;
            }
            inputs.Add(new CodexTextInput($"[{message.Role.ToString().ToLowerInvariant()}]"));
            foreach (var part in message.Content)
                inputs.Add(part switch
                {
                    TextContent text => (CodexInput)new CodexTextInput(text.Text),
                    ImageContent image => new CodexImageInput(image.Url),
                    _ => throw new ArgumentException("Unsupported content block.")
                });
        }
        if (inputs.Count == 0) throw new ArgumentException("Codex needs a user input.");
        return (instructions.Count > 0 ? string.Join('\n', instructions) : null, inputs);
    }

    public async Task<string> GenerateAsync(ProviderSettings settings, IReadOnlyList<LmmMessage> messages, CancellationToken cancellationToken)
    {
        settings.Validate(requireModel: true);
        using var deadline = Deadline(settings, cancellationToken);
        await using var client = await CodexRpcClient.StartAsync(settings.CodexRuntimePath, deadline.Token);
        await RequireAccountAsync(client, deadline.Token);
        var (instructions, inputs) = ConvertMessages(messages);
        var started = await client.RequestAsync("thread/start", new
        {
            model = settings.Model, developerInstructions = instructions, ephemeral = true,
            approvalPolicy = "never", sandbox = "read-only"
        }, deadline.Token);
        var threadId = started.GetProperty("thread").GetProperty("id").GetString();
        var turn = await client.RequestAsync("turn/start", new { threadId, input = inputs, effort = settings.ReasoningEffort }, deadline.Token);
        var turnId = turn.GetProperty("turn").GetProperty("id").GetString();
        string? final = null;
        await foreach (var notification in client.Notifications(deadline.Token))
        {
            if (!notification.TryGetProperty("method", out var methodElement) || !notification.TryGetProperty("params", out var parameters)) continue;
            var method = methodElement.GetString();
            if (parameters.TryGetProperty("threadId", out var tid) && tid.GetString() != threadId) continue;
            if (method == "item/completed" && parameters.GetProperty("turnId").GetString() == turnId)
            {
                var item = parameters.GetProperty("item");
                if (item.GetProperty("type").GetString() == "agentMessage" &&
                    (!item.TryGetProperty("phase", out var phase) || phase.ValueKind == JsonValueKind.Null || phase.GetString() == "final_answer"))
                    final = item.GetProperty("text").GetString();
            }
            if (method == "turn/completed")
            {
                var completed = parameters.GetProperty("turn");
                if (completed.GetProperty("id").GetString() != turnId) continue;
                if (completed.GetProperty("status").GetString() != "completed") throw new InvalidOperationException("Codex turn did not complete successfully.");
                return !string.IsNullOrWhiteSpace(final) ? final : throw new InvalidOperationException("Codex returned no final response.");
            }
        }
        throw new IOException("Codex closed before completing the turn.");
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(settings, cancellationToken);
        await using var client = await CodexRpcClient.StartAsync(settings.CodexRuntimePath, deadline.Token);
        await RequireAccountAsync(client, deadline.Token);
        var response = await client.RequestAsync("model/list", new { includeHidden = false }, deadline.Token);
        return response.GetProperty("data").EnumerateArray().Select(model => model.GetProperty("id").GetString()!).ToArray();
    }

    public async Task<CodexAccount> AccountAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(settings, cancellationToken);
        await using var client = await CodexRpcClient.StartAsync(settings.CodexRuntimePath, deadline.Token);
        var response = await client.RequestAsync("account/read", new { refreshToken = false }, deadline.Token);
        var account = response.GetProperty("account");
        var signedIn = account.ValueKind != JsonValueKind.Null && account.GetProperty("type").GetString() == "chatgpt";
        return new(signedIn, signedIn && account.TryGetProperty("email", out var email) ? email.GetString() ?? "" : "");
    }

    public async Task LoginAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        await using var client = await CodexRpcClient.StartAsync(settings.CodexRuntimePath, cancellationToken);
        var response = await client.RequestAsync("account/login/start", new { type = "chatgpt" }, cancellationToken);
        var url = response.GetProperty("authUrl").GetString()!;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        await foreach (var message in client.Notifications(cancellationToken))
            if (message.GetProperty("method").GetString() == "account/login/completed")
            {
                if (!message.GetProperty("params").GetProperty("success").GetBoolean()) throw new InvalidOperationException("ChatGPT login failed.");
                return;
            }
        throw new IOException("Login transport closed.");
    }

    public async Task LogoutAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        using var deadline = Deadline(settings, cancellationToken);
        await using var client = await CodexRpcClient.StartAsync(settings.CodexRuntimePath, deadline.Token);
        await client.RequestAsync("account/logout", null, deadline.Token);
    }

    private static async Task RequireAccountAsync(CodexRpcClient client, CancellationToken cancellationToken)
    {
        var response = await client.RequestAsync("account/read", new { refreshToken = false }, cancellationToken);
        var account = response.GetProperty("account");
        if (account.ValueKind == JsonValueKind.Null || account.GetProperty("type").GetString() != "chatgpt") throw new InvalidOperationException("Sign in to your ChatGPT account first.");
    }

    private static CancellationTokenSource Deadline(ProviderSettings settings, CancellationToken token)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(token);
        source.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        return source;
    }
}

public sealed class ProviderRouter(HttpLmmProvider http, CodexProvider codex) : ILmmProvider
{
    private ILmmProvider Resolve(ProviderSettings settings) => settings.Kind == ProviderKind.Codex ? codex : http;
    public Task<string> GenerateAsync(ProviderSettings settings, IReadOnlyList<LmmMessage> messages, CancellationToken cancellationToken) => Resolve(settings).GenerateAsync(settings, messages, cancellationToken);
    public Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings settings, CancellationToken cancellationToken) => Resolve(settings).ListModelsAsync(settings, cancellationToken);
}
