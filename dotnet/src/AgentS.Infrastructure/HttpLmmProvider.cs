using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentS.Core;

namespace AgentS.Infrastructure;

public sealed class HttpLmmProvider(HttpClient http) : ILmmProvider
{
    public async Task<string> GenerateAsync(ProviderSettings settings, IReadOnlyList<LmmMessage> messages, CancellationToken cancellationToken)
    {
        settings.Validate(requireModel: true);
        var anthropic = settings.Kind == ProviderKind.Anthropic;
        var payload = anthropic ? AnthropicPayload(settings, messages) : OpenAiPayload(settings, messages);
        using var document = await RequestAsync(settings, HttpMethod.Post, anthropic ? "messages" : "chat/completions", payload, cancellationToken);
        var root = document.RootElement;
        var text = anthropic
            ? string.Join('\n', root.GetProperty("content").EnumerateArray().Where(part => part.GetProperty("type").GetString() == "text").Select(part => part.GetProperty("text").GetString()))
            : root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Provider returned no assistant text.");
        return text;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (settings.Kind == ProviderKind.Azure)
            throw new ArgumentException("Enter the Azure deployment ID manually; model catalog IDs are not deployment IDs.");
        using var document = await RequestAsync(settings, HttpMethod.Get, "models", null, cancellationToken);
        if (document.RootElement.TryGetProperty("models", out var geminiModels))
            return geminiModels.EnumerateArray().Select(model => (model.GetProperty("name").GetString() ?? "").Replace("models/", "", StringComparison.Ordinal)).Take(1000).ToArray();
        var values = document.RootElement.GetProperty("data").EnumerateArray()
            .Select(model => model.GetProperty("id").GetString() ?? throw new InvalidOperationException("Missing model ID.")).Take(1000).ToArray();
        return values;
    }

    public static object OpenAiPayload(ProviderSettings settings, IReadOnlyList<LmmMessage> messages) => new
    {
        model = settings.Model,
        messages = messages.Select(message => new
        {
            role = message.Role.ToString().ToLowerInvariant(),
            content = message.Content.Select<MessageContent, object>(part => part switch
            {
                TextContent text => new { type = "text", text = text.Text },
                ImageContent image => new { type = "image_url", image_url = new { url = image.Url, detail = image.Detail } },
                _ => throw new ArgumentException("Unsupported content block.")
            }).ToArray()
        }).ToArray(),
        temperature = settings.Temperature
    };

    public static object AnthropicPayload(ProviderSettings settings, IReadOnlyList<LmmMessage> messages) => new
    {
        model = settings.Model,
        system = string.Join('\n', messages.Where(message => message.Role == MessageRole.System).Select(message => message.TextValue)),
        max_tokens = 4096,
        temperature = settings.Temperature,
        messages = messages.Where(message => message.Role != MessageRole.System).Select(message => new
        {
            role = message.Role.ToString().ToLowerInvariant(),
            content = message.Content.Select<MessageContent, object>(part => part switch
            {
                TextContent text => new { type = "text", text = text.Text },
                ImageContent image => AnthropicImage(image),
                _ => throw new ArgumentException("Unsupported content block.")
            }).ToArray()
        }).ToArray()
    };

    private static object AnthropicImage(ImageContent image)
    {
        var comma = image.Url.IndexOf(',', StringComparison.Ordinal);
        if (!image.Url.StartsWith("data:image/", StringComparison.Ordinal) || comma < 0 || !image.Url[..comma].EndsWith(";base64", StringComparison.Ordinal))
            throw new ArgumentException("Anthropic images must be base64 image data URLs.");
        return new { type = "image", source = new { type = "base64", media_type = image.Url[5..image.Url.IndexOf(';', StringComparison.Ordinal)], data = image.Url[(comma + 1)..] } };
    }

    private async Task<JsonDocument> RequestAsync(ProviderSettings settings, HttpMethod method, string route, object? payload, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var key = ProviderCatalog.ResolveApiKey(settings);
        var baseUrl = settings.BaseUrl.TrimEnd('/');
        var nativeGeminiDiscovery = settings.Kind == ProviderKind.Gemini && route == "models" && baseUrl.EndsWith("/openai", StringComparison.Ordinal);
        if (nativeGeminiDiscovery) baseUrl = baseUrl[..^7];
        var url = settings.Kind == ProviderKind.Azure
            ? route == "models" ? $"{baseUrl}/openai/models?api-version={Uri.EscapeDataString(settings.ApiVersion)}"
                : $"{baseUrl}/openai/deployments/{Uri.EscapeDataString(settings.Model)}/{route}?api-version={Uri.EscapeDataString(settings.ApiVersion)}"
            : $"{baseUrl}/{route}";
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, url);
            if (settings.Kind == ProviderKind.Anthropic)
            {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else if (settings.Kind == ProviderKind.Azure) request.Headers.Add("api-key", key);
            else if (nativeGeminiDiscovery) request.Headers.Add("x-goog-api-key", key);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (payload is not null) request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (attempt < 2 && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt + 1);
                await Task.Delay(delay > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delay, deadline.Token);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"{settings.Kind} returned HTTP {(int)response.StatusCode}. Check endpoint, model, and credentials.", null, response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var memory = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (memory.Length + read > 8 * 1024 * 1024) throw new InvalidOperationException("Provider response exceeds 8 MiB.");
                memory.Write(buffer, 0, read);
            }
            return JsonDocument.Parse(memory.ToArray());
        }
    }
}
