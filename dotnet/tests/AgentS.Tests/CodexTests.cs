using AgentS.Core;
using AgentS.Infrastructure;

namespace AgentS.Tests;

public sealed class CodexTests
{
    private static ProviderSettings Settings => new()
    {
        Kind = ProviderKind.Codex, Model = "gpt-5.6-luna", ReasoningEffort = "low",
        CodexRuntimePath = Path.Combine(AppContext.BaseDirectory, "fake-codex", "AgentS.FakeCodex.exe")
    };

    [Fact]
    public async Task NativeCodexSdkTransportHandlesCollidingServerRequestsAndEarlyCompletion()
    {
        var result = await new CodexProvider().GenerateAsync(Settings,
            [LmmMessage.Text(MessageRole.System, "instructions"), LmmMessage.Observation("look", [1])], CancellationToken.None);
        Assert.Equal("fake final response", result);
    }

    [Fact]
    public async Task CodexAccountAndDiscoveryUseSdkProtocolWithoutGeneration()
    {
        var provider = new CodexProvider();
        Assert.True((await provider.AccountAsync(Settings, CancellationToken.None)).SignedIn);
        Assert.Equal(new[] { "gpt-5.6-luna" }, await provider.ListModelsAsync(Settings, CancellationToken.None));
        await provider.LogoutAsync(Settings, CancellationToken.None);
    }
}
