using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentS.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentS.Tests;

[CollectionDefinition("Backend environment", DisableParallelization = true)]
public sealed class BackendEnvironmentCollection;

[Collection("Backend environment")]
public sealed class ApiTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly string? previousState = Environment.GetEnvironmentVariable("AGENT_S_DOTNET_STATE");
    private readonly string? previousToken = Environment.GetEnvironmentVariable("AGENT_S_DOTNET_TOKEN");
    private readonly Factory factory;
    private readonly HttpClient client;

    public ApiTests()
    {
        Environment.SetEnvironmentVariable("AGENT_S_DOTNET_STATE", directory.Path);
        Environment.SetEnvironmentVariable("AGENT_S_DOTNET_TOKEN", "test-token");
        factory = new();
        client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
    }

    [Fact]
    public async Task EveryDesktopApiRequiresAuthentication()
    {
        client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/models")).StatusCode);
    }

    [Fact]
    public async Task ProfilesAreStableMaskedAndRetainBlankSecrets()
    {
        var profiles = (await client.GetFromJsonAsync<ProfileCollection>("/api/profiles", AgentJson.Options))!;
        var active = profiles.Active;
        var updated = active with { Settings = active.Settings with { Planner = active.Settings.Planner with { ApiKey = "private-key" } } };
        var response = await client.PutAsJsonAsync("/api/profiles", new ProfileUpdate(profiles with { Profiles = [updated] }), AgentJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("private-key", await response.Content.ReadAsStringAsync());
        var masked = (await response.Content.ReadFromJsonAsync<ProfileCollection>(AgentJson.Options))!;
        Assert.Equal(active.Id, masked.Active.Id);
        var preserved = await client.PutAsJsonAsync("/api/profiles", new ProfileUpdate(masked), AgentJson.Options);
        Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
        using var scope = factory.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IProfileStore>().LoadAsync();
        Assert.Equal("private-key", stored.Active.Settings.Planner.ApiKey);
    }

    [Fact]
    public async Task DiscoveryDoesNotGenerateAndReturnsTypedModels()
    {
        var response = await client.PostAsJsonAsync("/api/connection/test", new ConnectionRequest());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("mock-model", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Provider.Calls);
    }

    [Fact]
    public async Task KeysDoNotFollowChangedEndpointsAndClearingOnlyAffectsActiveProfile()
    {
        var profiles = (await client.GetFromJsonAsync<ProfileCollection>("/api/profiles", AgentJson.Options))!;
        var first = profiles.Active with { Settings = profiles.Active.Settings with { Planner = profiles.Active.Settings.Planner with { ApiKey = "first-key" } } };
        var second = first with { Id = Guid.NewGuid(), Name = "Second", Settings = first.Settings with { Planner = first.Settings.Planner with { ApiKey = "second-key" } } };
        (await client.PutAsJsonAsync("/api/profiles", new ProfileUpdate(new(first.Id, [first, second])), AgentJson.Options)).EnsureSuccessStatusCode();
        var masked = (await client.GetFromJsonAsync<ProfileCollection>("/api/profiles", AgentJson.Options))!;
        (await client.PutAsJsonAsync("/api/profiles", new ProfileUpdate(masked, ClearPlannerKey: true), AgentJson.Options)).EnsureSuccessStatusCode();
        var store = factory.Services.GetRequiredService<IProfileStore>();
        var stored = await store.LoadAsync();
        Assert.Empty(stored.Active.Settings.Planner.ApiKey);
        Assert.Equal("second-key", stored.Profiles.Single(profile => profile.Id == second.Id).Settings.Planner.ApiKey);
        var changed = second with { Settings = second.Settings with { Planner = second.Settings.Planner with { ApiKey = "", BaseUrl = "https://different.example/v1" } } };
        (await client.PutAsJsonAsync("/api/profiles", new ProfileUpdate(masked with { Profiles = [masked.Active, changed] }), AgentJson.Options)).EnsureSuccessStatusCode();
        Assert.Empty((await store.LoadAsync()).Profiles.Single(profile => profile.Id == second.Id).Settings.Planner.ApiKey);
    }

    [Fact]
    public async Task DryRunApiReturnsPersistableResultWithoutDesktopExecution()
    {
        var profiles = (await client.GetFromJsonAsync<ProfileCollection>("/api/profiles", AgentJson.Options))!;
        var active = profiles.Active with { Settings = new() { Planner = new() { Model = "mock" }, Grounding = new() { Model = "mock" }, EnableReflection = false } };
        (await client.PutAsJsonAsync("/api/profiles", new ProfileUpdate(profiles with { Profiles = [active] }), AgentJson.Options)).EnsureSuccessStatusCode();
        var response = await client.PostAsJsonAsync("/api/tasks", new LaunchRequest("test"), AgentJson.Options);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var launched = (await response.Content.ReadFromJsonAsync<TaskSnapshot>(AgentJson.Options))!;
        var result = await factory.Services.GetRequiredService<AgentTaskManager>().WaitAsync(launched.Id);
        Assert.Equal("dryRunPreview", result.DoneReason);
        Assert.Equal(0, factory.Desktop.Executions);
        var status = await client.GetAsync($"/api/tasks/{result.Id}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var bytes = await client.GetByteArrayAsync($"/api/tasks/{result.Id}/screenshot");
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    private sealed class Factory : WebApplicationFactory<Program>
    {
        internal FakeProvider Provider { get; } = new(FakeProvider.Decision(ActionKind.Wait));
        internal FakeDesktop Desktop { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILmmProvider>();
            services.RemoveAll<IDesktop>();
            services.AddSingleton<ILmmProvider>(Provider);
            services.AddSingleton<IDesktop>(Desktop);
        });
    }

    public void Dispose()
    {
        client.Dispose();
        factory.Dispose();
        Environment.SetEnvironmentVariable("AGENT_S_DOTNET_STATE", previousState);
        Environment.SetEnvironmentVariable("AGENT_S_DOTNET_TOKEN", previousToken);
        directory.Dispose();
    }
}
