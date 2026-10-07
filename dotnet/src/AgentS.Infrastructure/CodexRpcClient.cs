using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using AgentS.Core;

namespace AgentS.Infrastructure;

/// <summary>Native, multiplexed client for Codex app-server's newline-delimited SDK protocol.</summary>
public sealed class CodexRpcClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = [];
    private readonly Channel<JsonElement> notifications = Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(1024)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task reader;
    private readonly Task stderr;
    private long nextId;

    private CodexRpcClient(Process process)
    {
        this.process = process;
        reader = ReadAsync();
        stderr = DrainErrorsAsync();
    }

    public static async Task<CodexRpcClient> StartAsync(string runtimePath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(runtimePath)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "app-server", "-c", "features.shell_tool=false", "-c", "forced_login_method=\"chatgpt\"" }) start.ArgumentList.Add(arg);
        var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex runtime.");
        var client = new CodexRpcClient(process);
        try
        {
            await client.RequestAsync("initialize", new
            {
                clientInfo = new { name = "agent_s_dotnet", title = "Agent S .NET", version = "1.0.0" },
                capabilities = new { experimentalApi = true }
            }, cancellationToken);
            await client.WriteAsync(new { method = "initialized" }, cancellationToken);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate request ID.");
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally { pending.TryRemove(id, out _); }
    }

    public IAsyncEnumerable<JsonElement> Notifications(CancellationToken cancellationToken) => notifications.Reader.ReadAllAsync(cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(message);
        await writer.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    private async Task ReadAsync()
    {
        Exception? failure = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 8 * 1024 * 1024) throw new InvalidDataException("Codex message exceeds limits.");
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("method", out _) && root.TryGetProperty("id", out var id) && id.TryGetInt64(out var number) && pending.TryGetValue(number, out var completion))
                {
                    if (root.TryGetProperty("error", out var error)) completion.TrySetException(new InvalidOperationException(error.GetProperty("message").GetString()));
                    else completion.TrySetResult(root.GetProperty("result").Clone());
                }
                else if (root.TryGetProperty("id", out var serverId) && root.TryGetProperty("method", out _))
                {
                    // This planner never authorizes app-server tool/permission requests.
                    await WriteAsync(new { id = serverId.Clone(), error = new { code = -32601, message = "Client tool requests are disabled for desktop planning." } }, lifetime.Token);
                }
                else await notifications.Writer.WriteAsync(root.Clone(), lifetime.Token);
            }
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or InvalidDataException) { failure = error; }
        finally
        {
            var error = failure ?? new IOException("Codex transport closed.");
            foreach (var request in pending.Values) request.TrySetException(error);
            notifications.Writer.TryComplete(failure);
        }
    }

    private async Task DrainErrorsAsync()
    {
        try { while (await process.StandardError.ReadLineAsync(lifetime.Token) is not null) { } }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await Task.WhenAll(reader, stderr);
        process.Dispose();
        lifetime.Dispose();
        writer.Dispose();
    }
}
