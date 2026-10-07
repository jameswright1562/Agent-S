using System.Collections.Concurrent;
using System.Text.Json;
using AgentS.Core;

namespace AgentS.Tests;

internal sealed class FakeProvider(params string[] responses) : ILmmProvider
{
    private readonly ConcurrentQueue<string> responses = new(responses);
    internal int Calls;
    public Task<string> GenerateAsync(ProviderSettings settings, IReadOnlyList<LmmMessage> messages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Calls);
        return Task.FromResult(this.responses.TryDequeue(out var response) ? response : throw new InvalidOperationException("Unexpected model call."));
    }
    public Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings settings, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["mock-model"]);
    internal static string Decision(ActionKind kind, double seconds = 0) => JsonSerializer.Serialize(new PlannerDecision("Verified mock observation.", new() { Kind = kind, Seconds = seconds }), AgentJson.Options);
}

internal sealed class BlockingProvider : ILmmProvider
{
    internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<string> GenerateAsync(ProviderSettings settings, IReadOnlyList<LmmMessage> messages, CancellationToken cancellationToken)
    {
        Entered.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return "unreachable";
    }
    public Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings settings, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class FakeDesktop : IDesktop
{
    internal int Executions;
    internal int Leases;
    public IReadOnlyList<MonitorInfo> GetMonitors() => [new(1, -1920, 0, 1920, 1080, "Mock monitor")];
    public Task<Screenshot> CaptureAsync(int monitor, CancellationToken cancellationToken) => Task.FromResult(new Screenshot([1, 2, 3], GetMonitors()[0], DateTimeOffset.UtcNow, 1));
    public Task<IReadOnlyList<OcrWord>> RecognizeTextAsync(Screenshot screenshot, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<OcrWord>>([new("Mock", 10, 20, 40, 20)]);
    public Task ExecuteAsync(PreparedAction action, Screenshot observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref Executions);
        return Task.CompletedTask;
    }
    public IDisposable AcquireLease() { Interlocked.Increment(ref Leases); return new Lease(() => Interlocked.Decrement(ref Leases)); }
    private sealed class Lease(Action release) : IDisposable { public void Dispose() => release(); }
}

internal sealed class FakeExecutor : ICodeExecutor
{
    public Task<CodeExecutionResult> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(new CodeExecutionResult(0, "mock output", ""));
}

internal sealed class MemoryArchive : ITaskArchive
{
    internal ConcurrentQueue<TaskSnapshot> Saved { get; } = [];
    public Task SaveAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default) { Saved.Enqueue(snapshot); return Task.CompletedTask; }
    public Task<IReadOnlyList<TaskSnapshot>> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<TaskSnapshot>>(Saved.GroupBy(task => task.Id).Select(group => group.Last()).ToArray());
}

internal sealed class TempDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "test-state", Guid.NewGuid().ToString("N"));
    internal TempDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
