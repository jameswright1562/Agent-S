using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AgentS.Core;

public enum TaskState { Queued, Planning, AwaitingApproval, Executing, Stopping, Completed, Failed, Stopped }
public enum StepState { Planned, AwaitingApproval, Approved, Rejected, Executed, DryRun, Finished, Cancelled, Error }
public sealed record StepRecord(int Number, string Analysis, string Reflection, PreparedAction PreparedAction, StepState State, DateTimeOffset Timestamp, string? Error = null);
public sealed record PendingApproval(int Step, Guid Token);
public sealed record TaskSnapshot(Guid Id, string Instruction, Guid ProfileId, string ProfileName, TaskState State,
    int MaxSteps, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, ImmutableArray<StepRecord> Steps,
    PendingApproval? PendingApproval = null, string? DoneReason = null, string? Error = null, long Version = 0, int ScreenshotSequence = 0)
{
    [JsonIgnore] public bool IsActive => State is TaskState.Queued or TaskState.Planning or TaskState.AwaitingApproval or TaskState.Executing or TaskState.Stopping;
    [JsonIgnore] public byte[]? Screenshot { get; init; }
    public AgentSettings? SettingsSnapshot { get; init; }
}

public sealed class TaskConflictException(string message) : InvalidOperationException(message);

public sealed class AgentTaskManager(ILmmProvider provider, IDesktop desktop, ICodeExecutor codeExecutor, ITaskArchive archive) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, TaskSnapshot> tasks = [];
    private CancellationTokenSource? cancellation;
    private Task? execution;
    private TaskCompletionSource<bool>? approval;
    private bool disposed;
    public event EventHandler<TaskSnapshot>? Changed;

    public IReadOnlyList<TaskSnapshot> List()
    {
        lock (gate) return tasks.Values.OrderByDescending(task => task.CreatedAt).ToArray();
    }

    public TaskSnapshot Get(Guid id)
    {
        lock (gate) return tasks.TryGetValue(id, out var task) ? task : throw new KeyNotFoundException("Task not found.");
    }

    public async Task RestoreHistoryAsync(CancellationToken cancellationToken = default)
    {
        var history = await archive.LoadAsync(cancellationToken);
        lock (gate)
            foreach (var task in history.Take(25)) tasks.TryAdd(task.Id, task.IsActive
                ? task with
                {
                    State = TaskState.Stopped, FinishedAt = DateTimeOffset.UtcNow, DoneReason = "interruptedByRestart", PendingApproval = null,
                    Version = task.Version + 1,
                    Steps = task.Steps.Select(step => step.State is StepState.Planned or StepState.AwaitingApproval or StepState.Approved
                        ? step with { State = StepState.Cancelled } : step).ToImmutableArray()
                }
                : task);
    }

    public TaskSnapshot Launch(string instruction, Profile profile)
    {
        if (string.IsNullOrWhiteSpace(instruction) || instruction.Length > 32768) throw new ArgumentException("Provide a task instruction of at most 32768 characters.");
        profile.Settings.Validate(requireModels: true);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (execution is { IsCompleted: false }) throw new TaskConflictException("Another desktop task is active.");
            foreach (var old in tasks.Values.Where(task => !task.IsActive).OrderByDescending(task => task.CreatedAt).Skip(24).ToArray()) tasks.Remove(old.Id);
            var snapshot = new TaskSnapshot(Guid.NewGuid(), instruction.Trim(), profile.Id, profile.Name, TaskState.Queued,
                profile.Settings.MaxSteps, DateTimeOffset.UtcNow, null, []) { SettingsSnapshot = profile.Settings.WithoutSecrets() };
            tasks.Add(snapshot.Id, snapshot);
            cancellation?.Dispose();
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(profile.Settings.TaskTimeoutSeconds));
            var token = cancellation.Token;
            execution = Task.Run(() => ExecuteAsync(snapshot.Id, profile.Settings, token), CancellationToken.None);
            return snapshot;
        }
    }

    public void Stop(Guid id)
    {
        lock (gate)
        {
            if (!Get(id).IsActive) return;
            Update(id, task => task with { State = TaskState.Stopping, PendingApproval = null });
            cancellation?.Cancel();
        }
    }

    public void Approve(Guid id, int step, Guid token, bool approved)
    {
        lock (gate)
        {
            var task = Get(id);
            if (task.State != TaskState.AwaitingApproval || task.PendingApproval != new PendingApproval(step, token) || approval is null)
                throw new TaskConflictException("Approval is stale or does not match the pending step.");
            if (!approval.TrySetResult(approved)) throw new TaskConflictException("The step has already been decided.");
        }
    }

    public async Task<TaskSnapshot> WaitAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Task? running;
        lock (gate) { _ = Get(id); running = execution; }
        if (Get(id).IsActive && running is not null) await running.WaitAsync(cancellationToken);
        return Get(id);
    }

    private void Update(Guid id, Func<TaskSnapshot, TaskSnapshot> update)
    {
        TaskSnapshot changed;
        lock (gate) { changed = update(tasks[id]) with { Version = tasks[id].Version + 1 }; tasks[id] = changed; }
        Changed?.Invoke(this, changed);
    }

    private void SetStep(Guid id, StepRecord step, TaskState state, PendingApproval? pending = null) => Update(id, task => task with
    {
        State = state,
        PendingApproval = pending,
        Steps = task.Steps.Where(existing => existing.Number != step.Number).Append(step).OrderBy(existing => existing.Number).ToImmutableArray()
    });

    private async Task ExecuteAsync(Guid id, AgentSettings settings, CancellationToken cancellationToken)
    {
        StepRecord? stepRecord = null;
        try
        {
            using var lease = desktop.AcquireLease();
            await archive.SaveAsync(Get(id), cancellationToken);
            var grounding = new GroundingService(provider, desktop);
            var planner = new S3Planner(provider, settings, Get(id).Instruction);
            for (int number = 1; number <= settings.MaxSteps; number++)
            {
                stepRecord = null;
                cancellationToken.ThrowIfCancellationRequested();
                Update(id, task => task with { State = TaskState.Planning, PendingApproval = null });
                var screenshot = await desktop.CaptureAsync(settings.Monitor, cancellationToken);
                Update(id, task => task with { Screenshot = screenshot.Png, ScreenshotSequence = task.ScreenshotSequence + 1 });
                var plan = await planner.PlanAsync(screenshot, cancellationToken);
                var prepared = await grounding.PrepareAsync(plan.Decision.Action, screenshot, settings, cancellationToken);
                stepRecord = new(number, plan.Decision.Analysis, plan.Reflection, prepared, StepState.Planned, DateTimeOffset.UtcNow);
                SetStep(id, stepRecord, TaskState.Planning);
                await archive.SaveAsync(Get(id), cancellationToken);
                if (prepared.Action.Kind is ActionKind.Done or ActionKind.Fail)
                {
                    SetStep(id, stepRecord = stepRecord with { State = StepState.Finished }, TaskState.Planning);
                    Complete(id, prepared.Action.Kind == ActionKind.Done ? TaskState.Completed : TaskState.Failed, prepared.Action.Kind.ToString());
                    return;
                }
                if (settings.Approval == ApprovalMode.DryRun)
                {
                    SetStep(id, stepRecord = stepRecord with { State = StepState.DryRun }, TaskState.Planning);
                    // A dry run cannot progress the desktop; stop after the preview rather than claiming completion.
                    Complete(id, TaskState.Completed, "dryRunPreview");
                    return;
                }
                if (settings.Approval == ApprovalMode.Manual)
                {
                    var pending = new PendingApproval(number, Guid.NewGuid());
                    TaskCompletionSource<bool> decision;
                    lock (gate) approval = decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    SetStep(id, stepRecord = stepRecord with { State = StepState.AwaitingApproval }, TaskState.AwaitingApproval, pending);
                    await archive.SaveAsync(Get(id), cancellationToken);
                    if (!await decision.Task.WaitAsync(cancellationToken))
                    {
                        SetStep(id, stepRecord = stepRecord with { State = StepState.Rejected }, TaskState.Stopping);
                        Complete(id, TaskState.Stopped, "stepRejected");
                        return;
                    }
                    lock (gate) approval = null;
                    SetStep(id, stepRecord = stepRecord with { State = StepState.Approved }, TaskState.Executing);
                }
                cancellationToken.ThrowIfCancellationRequested();
                Update(id, task => task with { State = TaskState.Executing, PendingApproval = null });
                using var actionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                actionDeadline.CancelAfter(TimeSpan.FromSeconds(settings.ActionTimeoutSeconds));
                string result = "Executed.";
                if (prepared.Action.Kind == ActionKind.CallCodeAgent)
                {
                    var codeResult = await new CodeAgent(provider, codeExecutor).RunAsync(prepared.Action.Task ?? Get(id).Instruction, screenshot, settings, actionDeadline.Token);
                    result = System.Text.Json.JsonSerializer.Serialize(codeResult, AgentJson.Options);
                }
                else if (prepared.Action.Kind != ActionKind.SaveToKnowledge)
                    await desktop.ExecuteAsync(prepared, screenshot, actionDeadline.Token);
                planner.ObserveResult(prepared, result);
                SetStep(id, stepRecord = stepRecord with { State = StepState.Executed }, TaskState.Planning);
                await archive.SaveAsync(Get(id), cancellationToken);
                await Task.Delay(500, cancellationToken);
            }
            Complete(id, TaskState.Stopped, "maxStepsReached");
        }
        catch (OperationCanceledException)
        {
            if (stepRecord is { State: StepState.Planned or StepState.AwaitingApproval or StepState.Approved })
                SetStep(id, stepRecord with { State = StepState.Cancelled }, TaskState.Stopping);
            Complete(id, TaskState.Stopped, "cancelledOrDeadlineReached");
        }
        catch (Exception error)
        {
            if (stepRecord is { State: StepState.Planned or StepState.AwaitingApproval or StepState.Approved })
                SetStep(id, stepRecord with { State = StepState.Error, Error = error.Message }, TaskState.Failed);
            Complete(id, TaskState.Failed, "error", error.Message);
        }
        finally
        {
            lock (gate) approval = null;
            try { await archive.SaveAsync(Get(id), CancellationToken.None); }
            catch (IOException error) { Update(id, task => task with { Error = $"{task.Error}\nHistory persistence failed: {error.Message}".Trim() }); }
        }
    }

    private void Complete(Guid id, TaskState state, string reason, string? error = null) => Update(id, task => task with
    {
        State = state, FinishedAt = DateTimeOffset.UtcNow, PendingApproval = null, DoneReason = reason, Error = error
    });

    public async ValueTask DisposeAsync()
    {
        Task? running;
        lock (gate) { disposed = true; cancellation?.Cancel(); running = execution; }
        if (running is not null) await running;
        cancellation?.Dispose();
    }
}
