using AgentS.Core;
using AgentS.Infrastructure;

public sealed class LoginCoordinator(CodexProvider provider, ILogger<LoginCoordinator> logger) : IAsyncDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? cancellation;
    private Task? running;
    private string status = "idle";
    private string? error;
    public string Status { get { lock (gate) return status; } }
    public string? Error { get { lock (gate) return error; } }

    public void Start(ProviderSettings settings)
    {
        lock (gate)
        {
            if (running is { IsCompleted: false }) throw new TaskConflictException("ChatGPT login is already active.");
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            status = "waitingForBrowser";
            error = null;
            var token = cancellation.Token;
            running = Task.Run(async () =>
            {
                try { await provider.LoginAsync(settings, token); lock (gate) status = "completed"; }
                catch (Exception failure)
                {
                    lock (gate) { status = "failed"; error = failure.Message; }
                    logger.LogWarning("Codex login did not complete: {Message}", failure.Message);
                }
            }, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        cancellation?.Cancel();
        if (running is not null) await running;
        cancellation?.Dispose();
    }
}
