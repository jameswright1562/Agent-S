using System.Net;
using System.Text;
using System.Text.Json;
using AgentS.Core;
using AgentS.Infrastructure;

namespace AgentS.Tests;

public sealed class ProviderTests
{
    [Fact]
    public async Task OpenAiAdapterPreservesRolesAndImageOrder()
    {
        JsonElement request = default;
        using var handler = new Handler(async message =>
        {
            Assert.Equal("secret", message.Headers.Authorization!.Parameter);
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
            request = json.RootElement.Clone();
            return Json("{\"choices\":[{\"message\":{\"content\":\"answer\"}}]}");
        });
        using var http = new HttpClient(handler);
        var answer = await new HttpLmmProvider(http).GenerateAsync(new() { Model = "test", ApiKey = "secret" },
            [LmmMessage.Text(MessageRole.System, "system"), LmmMessage.Observation("look", [1, 2], true)], CancellationToken.None);
        Assert.Equal("answer", answer);
        Assert.Equal("system", request.GetProperty("messages")[0].GetProperty("role").GetString());
        Assert.Equal("image_url", request.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task AnthropicAdapterExtractsSystemAndSkipsThinkingBlocks()
    {
        using var handler = new Handler(async request =>
        {
            Assert.Equal("secret", Assert.Single(request.Headers.GetValues("x-api-key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("system", json.RootElement.GetProperty("system").GetString());
            Assert.Equal(1, json.RootElement.GetProperty("messages").GetArrayLength());
            return Json("{\"content\":[{\"type\":\"thinking\",\"thinking\":\"private\"},{\"type\":\"text\",\"text\":\"answer\"}]}");
        });
        using var http = new HttpClient(handler);
        var result = await new HttpLmmProvider(http).GenerateAsync(new() { Kind = ProviderKind.Anthropic, Model = "test", ApiKey = "secret" },
            [LmmMessage.Text(MessageRole.System, "system"), LmmMessage.Observation("look", [1])], CancellationToken.None);
        Assert.Equal("answer", result);
    }

    [Fact]
    public async Task ModelDiscoveryMakesOnlyOneGetAndNoGeneration()
    {
        int requests = 0;
        using var handler = new Handler(request =>
        {
            requests++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.EndsWith("/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("{\"data\":[{\"id\":\"one\"},{\"id\":\"two\"}]}"));
        });
        using var http = new HttpClient(handler);
        var models = await new HttpLmmProvider(http).ListModelsAsync(new() { ApiKey = "secret" }, CancellationToken.None);
        Assert.Equal(new[] { "one", "two" }, models);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ProviderErrorsDoNotExposeApiKeysOrResponseBodies()
    {
        using var handler = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret") }));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new HttpLmmProvider(http).ListModelsAsync(new() { ApiKey = "secret" }, CancellationToken.None));
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task GeminiDiscoveryUsesNativeCatalogWithoutPuttingKeysInUrls()
    {
        using var handler = new Handler(request =>
        {
            Assert.Equal("/v1beta/models", request.RequestUri!.AbsolutePath);
            Assert.Empty(request.RequestUri.Query);
            Assert.Equal("secret", Assert.Single(request.Headers.GetValues("x-goog-api-key")));
            return Task.FromResult(Json("{\"models\":[{\"name\":\"models/gemini-test\"}]}"));
        });
        using var http = new HttpClient(handler);
        var result = await new HttpLmmProvider(http).ListModelsAsync(new() { Kind = ProviderKind.Gemini, BaseUrl = "https://generativelanguage.googleapis.com/v1beta/openai", ApiKey = "secret" }, CancellationToken.None);
        Assert.Equal(new[] { "gemini-test" }, result);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
