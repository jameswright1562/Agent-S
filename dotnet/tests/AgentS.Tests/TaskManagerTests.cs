using AgentS.Core;

namespace AgentS.Tests;

public sealed class TaskManagerTests
{
    private static Profile Profile(ApprovalMode mode) => new(Guid.NewGuid(), "Test", new()
    {
        Planner = new() { Model = "mock" }, Grounding = new() { Model = "mock" }, EnableReflection = false, Approval = mode
    });

    [Fact]
    public async Task DryRunPreparesOnePreviewAndNeverExecutesInput()
    {
        var provider = new FakeProvider(FakeProvider.Decision(ActionKind.Wait));
        var desktop = new FakeDesktop();
        var archive = new MemoryArchive();
        await using var manager = new AgentTaskManager(provider, desktop, new FakeExecutor(), archive);
        var launched = manager.Launch("test", Profile(ApprovalMode.DryRun));
        var result = await manager.WaitAsync(launched.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("dryRunPreview", result.DoneReason);
        Assert.Equal(StepState.DryRun, Assert.Single(result.Steps).State);
        Assert.Equal(0, desktop.Executions);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(0, desktop.Leases);
        Assert.True(archive.Saved.Count >= 3);
        Assert.Equal(TaskState.Completed, archive.Saved.Last().State);
        Assert.True(result.Version > 0);
        Assert.Equal(1, result.ScreenshotSequence);
    }

    [Fact]
    public async Task StopAfterExecutionKeepsExecutedStepAndPersistsMaskedConfiguration()
    {
        var archive = new MemoryArchive();
        await using var manager = new AgentTaskManager(new FakeProvider(FakeProvider.Decision(ActionKind.Wait)), new FakeDesktop(), new FakeExecutor(), archive);
        manager.Changed += (_, task) => { if (task.State == TaskState.Planning && task.Steps.LastOrDefault()?.State == StepState.Executed) manager.Stop(task.Id); };
        var profile = Profile(ApprovalMode.Auto);
        profile = profile with { Settings = profile.Settings with { Planner = profile.Settings.Planner with { ApiKey = "private-secret" } } };
        var launched = manager.Launch("test", profile);
        var result = await manager.WaitAsync(launched.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskState.Stopped, result.State);
        Assert.Equal(StepState.Executed, Assert.Single(result.Steps).State);
        Assert.NotNull(result.SettingsSnapshot);
        Assert.Empty(result.SettingsSnapshot.Planner.ApiKey);
        Assert.DoesNotContain("private-secret", System.Text.Json.JsonSerializer.Serialize(result, AgentJson.Options));
    }

    [Fact]
    public async Task RestoredActiveCheckpointsAreStoppedAndPendingStepsCancelled()
    {
        var archive = new MemoryArchive();
        var checkpoint = new TaskSnapshot(Guid.NewGuid(), "test", Guid.NewGuid(), "profile", TaskState.AwaitingApproval, 15, DateTimeOffset.UtcNow, null,
            [new(1, "plan", "", new(new() { Kind = ActionKind.Wait }), StepState.AwaitingApproval, DateTimeOffset.UtcNow)], new(1, Guid.NewGuid()));
        await archive.SaveAsync(checkpoint);
        await using var manager = new AgentTaskManager(new FakeProvider(), new FakeDesktop(), new FakeExecutor(), archive);
        await manager.RestoreHistoryAsync();
        var restored = manager.Get(checkpoint.Id);
        Assert.Equal(TaskState.Stopped, restored.State);
        Assert.Equal("interruptedByRestart", restored.DoneReason);
        Assert.Null(restored.PendingApproval);
        Assert.Equal(StepState.Cancelled, Assert.Single(restored.Steps).State);
    }

    [Fact]
    public async Task ManualApprovalIsTokenBoundAndOnlyRunsOnce()
    {
        var desktop = new FakeDesktop();
        await using var manager = new AgentTaskManager(new FakeProvider(FakeProvider.Decision(ActionKind.Wait), FakeProvider.Decision(ActionKind.Done)), desktop, new FakeExecutor(), new MemoryArchive());
        var pending = new TaskCompletionSource<TaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += (_, task) => { if (task.PendingApproval is not null) pending.TrySetResult(task); };
        var launched = manager.Launch("test", Profile(ApprovalMode.Manual));
        var snapshot = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, desktop.Executions);
        Assert.Throws<TaskConflictException>(() => manager.Approve(launched.Id, 1, Guid.NewGuid(), true));
        Assert.Throws<TaskConflictException>(() => manager.Launch("second", Profile(ApprovalMode.Auto)));
        manager.Approve(launched.Id, 1, snapshot.PendingApproval!.Token, true);
        Assert.Throws<TaskConflictException>(() => manager.Approve(launched.Id, 1, snapshot.PendingApproval.Token, true));
        var result = await manager.WaitAsync(launched.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskState.Completed, result.State);
        Assert.Equal(1, desktop.Executions);
    }

    [Fact]
    public async Task RejectionStopsWithoutDesktopEffects()
    {
        var desktop = new FakeDesktop();
        await using var manager = new AgentTaskManager(new FakeProvider(FakeProvider.Decision(ActionKind.Wait)), desktop, new FakeExecutor(), new MemoryArchive());
        var pending = new TaskCompletionSource<TaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.Changed += (_, task) => { if (task.PendingApproval is not null) pending.TrySetResult(task); };
        var launched = manager.Launch("test", Profile(ApprovalMode.Manual));
        var task = await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Approve(task.Id, 1, task.PendingApproval!.Token, false);
        var result = await manager.WaitAsync(launched.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("stepRejected", result.DoneReason);
        Assert.Equal(0, desktop.Executions);
    }

    [Fact]
    public async Task CancellationReachesProviderAndReleasesDesktopLease()
    {
        var provider = new BlockingProvider();
        var desktop = new FakeDesktop();
        await using var manager = new AgentTaskManager(provider, desktop, new FakeExecutor(), new MemoryArchive());
        var launched = manager.Launch("test", Profile(ApprovalMode.Auto));
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.Stop(launched.Id);
        var result = await manager.WaitAsync(launched.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TaskState.Stopped, result.State);
        Assert.Equal(0, desktop.Leases);
        Assert.Equal(0, desktop.Executions);
    }

    [Fact]
    public async Task OneFormattingRetryCanRecoverWithoutExecutingBadOutput()
    {
        var provider = new FakeProvider("invalid", FakeProvider.Decision(ActionKind.Done));
        var desktop = new FakeDesktop();
        await using var manager = new AgentTaskManager(provider, desktop, new FakeExecutor(), new MemoryArchive());
        var task = manager.Launch("test", Profile(ApprovalMode.Auto));
        Assert.Equal(TaskState.Completed, (await manager.WaitAsync(task.Id)).State);
        Assert.Equal(2, provider.Calls);
        Assert.Equal(0, desktop.Executions);
    }
}
