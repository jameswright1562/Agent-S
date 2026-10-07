using AgentS.Core;
using AgentS.Windows;

public sealed class DesktopSafetyService(AgentTaskManager tasks, ILogger<DesktopSafetyService> logger) : IHostedService, IDisposable
{
    private DesktopStopHotkey? hotkey;
    public bool IsAvailable => hotkey?.IsAvailable == true;
    public Task StartAsync(CancellationToken cancellationToken)
    {
        hotkey = new(() =>
        {
            var task = tasks.List().FirstOrDefault(task => task.IsActive);
            if (task is not null)
            {
                try { tasks.Stop(task.Id); }
                catch (KeyNotFoundException) { /* The task finished and was pruned before the signal arrived. */ }
            }
        });
        if (!hotkey.IsAvailable) logger.LogWarning("Ctrl+Alt+Escape is already registered by another application.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { Dispose(); return Task.CompletedTask; }
    public void Dispose() { hotkey?.Dispose(); hotkey = null; }
}
