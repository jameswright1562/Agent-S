using System.Text.Json;
using AgentS.Client;
using AgentS.Cli;
using AgentS.Core;
using AgentS.Infrastructure;
using AgentS.Windows;

namespace AgentS.Tests;

public sealed class StorageAndProtocolTests
{
    [Fact]
    public async Task ProfileKeysAreEncryptedAtRestAndRoundTrip()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "profiles.json");
        using var store = new JsonProfileStore(path, new WindowsSecretProtector());
        var collection = await store.LoadAsync();
        var original = collection.Active;
        var profile = original with { Settings = original.Settings with { Planner = original.Settings.Planner with { ApiKey = "unit-test-secret" } } };
        await store.SaveAsync(collection with { Profiles = [profile] });
        Assert.DoesNotContain("unit-test-secret", await File.ReadAllTextAsync(path));
        Assert.Equal("unit-test-secret", (await store.LoadAsync()).Active.Settings.Planner.ApiKey);
        Assert.Equal(original.Id, (await store.LoadAsync()).Active.Id);
    }

    [Fact]
    public async Task ArchiveRoundTripsActionsWithoutEmbeddingScreenshots()
    {
        using var directory = new TempDirectory();
        var archive = new JsonTaskArchive(directory.Path);
        var task = new TaskSnapshot(Guid.NewGuid(), "test", Guid.NewGuid(), "profile", TaskState.Completed, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []) { Screenshot = [1, 2] };
        await archive.SaveAsync(task);
        var restored = Assert.Single(await archive.LoadAsync());
        Assert.Equal(task.Id, restored.Id);
        Assert.Equal(task.Screenshot, restored.Screenshot);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory.Path, task.Id.ToString("N") + ".json")));
        Assert.False(document.RootElement.TryGetProperty("screenshot", out _));
        Assert.True(File.Exists(Path.Combine(directory.Path, task.Id.ToString("N") + ".png")));
    }

    [Fact]
    public async Task McpHandshakeAndToolDiscoveryDoNotStartBackendOrGenerate()
    {
        using var client = new BackendClient();
        using var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}\n{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}\n");
        using var output = new StringWriter();
        await new McpServer(client).RunAsync(input, output, CancellationToken.None);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using var handshake = JsonDocument.Parse(lines[0]);
        Assert.Equal("agent-s-dotnet", handshake.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        using var tools = JsonDocument.Parse(lines[1]);
        Assert.Contains(tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("name").GetString() == "approve_step");
    }
}
