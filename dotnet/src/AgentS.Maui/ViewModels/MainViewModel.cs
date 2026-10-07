using System.Collections.ObjectModel;
using AgentS.Client;
using AgentS.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AgentS.Maui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly BackendClient client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private ProfileCollection? collection;
    private Task? polling;
    private Guid? displayedTask;
    private int displayedScreenshotStep = -1;
    private long displayedVersion = -1;
    private Guid? lastApproval;
    public Action? PrepareDesktop { get; set; }
    public Action? ApprovalRequested { get; set; }
    public ProviderEditorViewModel Planner { get; } = new();
    public ProviderEditorViewModel Grounding { get; } = new();
    public ObservableCollection<Profile> Profiles { get; } = [];
    public ObservableCollection<TaskSnapshot> Tasks { get; } = [];
    public ObservableCollection<StepRecord> Steps { get; } = [];
    public IReadOnlyList<ApprovalMode> ApprovalModes { get; } = Enum.GetValues<ApprovalMode>();
    [ObservableProperty] public partial Profile? SelectedProfile { get; set; }
    [ObservableProperty] public partial TaskSnapshot? SelectedTask { get; set; }
    [ObservableProperty] public partial string ProfileName { get; set; }
    [ObservableProperty] public partial string Instruction { get; set; }
    [ObservableProperty] public partial string Message { get; set; }
    [ObservableProperty] public partial string Status { get; set; }
    [ObservableProperty] public partial string TaskStatus { get; set; }
    [ObservableProperty] public partial string Preview { get; set; }
    [ObservableProperty] public partial string MaxSteps { get; set; }
    [ObservableProperty] public partial string Monitor { get; set; }
    [ObservableProperty] public partial string HistoryImages { get; set; }
    [ObservableProperty] public partial string TimeoutSeconds { get; set; }
    [ObservableProperty] public partial string GroundWidth { get; set; }
    [ObservableProperty] public partial string GroundHeight { get; set; }
    [ObservableProperty] public partial string TaskTimeout { get; set; }
    [ObservableProperty] public partial string ActionTimeout { get; set; }
    [ObservableProperty] public partial string CodeBudget { get; set; }
    [ObservableProperty] public partial bool EnableReflection { get; set; }
    [ObservableProperty] public partial bool EnableCodeAgent { get; set; }
    [ObservableProperty] public partial ApprovalMode Approval { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool HasApproval { get; set; }
    [ObservableProperty] public partial bool IsTaskActive { get; set; }
    [ObservableProperty] public partial ImageSource? Screenshot { get; set; }
    [ObservableProperty] public partial string AccountStatus { get; set; }

    public MainViewModel(BackendClient client)
    {
        this.client = client;
        ProfileName = "Default";
        Instruction = "";
        Message = "Connecting to the desktop backend…";
        Status = "Offline";
        TaskStatus = "Ready for a task";
        Preview = "No action is awaiting approval.";
        MaxSteps = "15";
        Monitor = "1";
        HistoryImages = "4";
        TimeoutSeconds = "60";
        GroundWidth = "1920";
        GroundHeight = "1080";
        TaskTimeout = "600";
        ActionTimeout = "60";
        CodeBudget = "20";
        EnableReflection = true;
        AccountStatus = "ChatGPT account status has not been checked.";
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (value is null) return;
        ProfileName = value.Name;
        Planner.Load(value.Settings.Planner);
        Grounding.Load(value.Settings.Grounding);
        MaxSteps = value.Settings.MaxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Monitor = value.Settings.Monitor.ToString(System.Globalization.CultureInfo.InvariantCulture);
        HistoryImages = value.Settings.HistoryImages.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TimeoutSeconds = value.Settings.Planner.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GroundWidth = value.Settings.GroundingWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        GroundHeight = value.Settings.GroundingHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TaskTimeout = value.Settings.TaskTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ActionTimeout = value.Settings.ActionTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CodeBudget = value.Settings.CodeAgentBudget.ToString(System.Globalization.CultureInfo.InvariantCulture);
        EnableReflection = value.Settings.EnableReflection;
        EnableCodeAgent = value.Settings.EnableCodeAgent;
        Approval = value.Settings.Approval;
    }

    [RelayCommand]
    private Task ConnectAsync() => RunAsync(async ct =>
    {
        await client.EnsureStartedAsync(ct);
        ApplyProfiles(await client.ProfilesAsync(ct));
        var status = await client.StatusAsync(ct);
        Status = $"Connected · {status.Monitors.Count} monitor(s)";
        Message = "Workspace ready. Configure planner and grounding models, then save your profile." +
            (status.EmergencyStopAvailable ? " Ctrl+Alt+Escape stops the active task." : " The emergency-stop hotkey is unavailable; use Stop task.");
        polling ??= PollAsync(lifetime.Token);
    });

    [RelayCommand]
    private Task SaveAsync() => RunAsync(SaveProfileAsync);

    private async Task SaveProfileAsync(CancellationToken ct)
    {
        if (collection is null || SelectedProfile is null) throw new InvalidOperationException("Connect first.");
        var settings = SelectedProfile.Settings with
        {
            Planner = Planner.Build() with { TimeoutSeconds = Number(TimeoutSeconds) },
            Grounding = Grounding.Build() with { TimeoutSeconds = Number(TimeoutSeconds) },
            MaxSteps = Number(MaxSteps), Monitor = Number(Monitor), HistoryImages = Number(HistoryImages),
            GroundingWidth = Number(GroundWidth), GroundingHeight = Number(GroundHeight),
            TaskTimeoutSeconds = Number(TaskTimeout), ActionTimeoutSeconds = Number(ActionTimeout), CodeAgentBudget = Number(CodeBudget),
            EnableReflection = EnableReflection, EnableCodeAgent = EnableCodeAgent, Approval = Approval
        };
        settings.Validate();
        var selected = SelectedProfile with { Name = ProfileName?.Trim() ?? "", Settings = settings };
        var updated = collection with { ActiveProfileId = selected.Id, Profiles = collection.Profiles.Select(profile => profile.Id == selected.Id ? selected : profile).ToArray() };
        ApplyProfiles(await client.SaveProfilesAsync(new(updated, Planner.ClearKey, Grounding.ClearKey), ct));
        Message = "Profile saved. API keys are encrypted for your Windows account.";
    }

    [RelayCommand]
    private Task AddProfileAsync() => RunAsync(async ct =>
    {
        if (collection is null) throw new InvalidOperationException("Connect first.");
        var profile = new Profile(Guid.NewGuid(), "New profile", SelectedProfile?.Settings ?? new());
        ApplyProfiles(await client.SaveProfilesAsync(new(collection with { ActiveProfileId = profile.Id, Profiles = [.. collection.Profiles, profile] }), ct));
    });

    [RelayCommand]
    private Task DeleteProfileAsync() => RunAsync(async ct =>
    {
        if (collection is null || SelectedProfile is null || collection.Profiles.Count <= 1) throw new InvalidOperationException("Keep at least one profile.");
        var remaining = collection.Profiles.Where(profile => profile.Id != SelectedProfile.Id).ToArray();
        ApplyProfiles(await client.SaveProfilesAsync(new(new(remaining[0].Id, remaining)), ct));
    });

    [RelayCommand]
    private Task LaunchAsync() => RunAsync(async ct =>
    {
        if (IsTaskActive) throw new InvalidOperationException("Stop the active task first.");
        if (string.IsNullOrWhiteSpace(Instruction)) throw new ArgumentException("Describe the task first.");
        await SaveProfileAsync(ct);
        PrepareDesktop?.Invoke();
        await Task.Delay(450, ct);
        var task = await client.LaunchAsync(new(Instruction, SelectedProfile!.Id), ct);
        displayedScreenshotStep = -1;
        displayedVersion = -1;
        displayedTask = task.Id;
        SelectedTask = task;
        Message = "Task started. The workspace is minimized so the agent observes your target application.";
    });

    [RelayCommand]
    private Task StopAsync() => RunAsync(async ct =>
    {
        var active = Tasks.FirstOrDefault(task => task.IsActive) ?? SelectedTask;
        if (active is not null) await client.StopAsync(active.Id, ct);
    });

    [RelayCommand] private Task ApproveAsync() => DecideAsync(true);
    [RelayCommand] private Task RejectAsync() => DecideAsync(false);
    private Task DecideAsync(bool approved) => RunAsync(async ct =>
    {
        var task = SelectedTask ?? throw new InvalidOperationException("Select the pending task.");
        var pending = task.PendingApproval ?? throw new InvalidOperationException("No pending approval.");
        if (approved) PrepareDesktop?.Invoke();
        await client.ApproveAsync(task.Id, new(pending.Step, pending.Token, approved), ct);
        HasApproval = false;
    });

    [RelayCommand] private Task PlannerModelsAsync() => FetchModelsAsync(Planner, false);
    [RelayCommand] private Task GroundingModelsAsync() => FetchModelsAsync(Grounding, true);
    private Task FetchModelsAsync(ProviderEditorViewModel editor, bool grounding) => RunAsync(async ct =>
    {
        var result = await client.ModelsAsync(new(grounding, editor.Build()), ct);
        editor.Models.Clear();
        foreach (var model in result.Models) editor.Models.Add(model);
        editor.SelectedModel = editor.Models.FirstOrDefault(model => model == editor.Model);
        Message = $"Discovered {result.Models.Count} models. No generation request was made.";
    });

    [RelayCommand]
    private Task CaptureAsync() => RunAsync(async ct =>
    {
        var bytes = await client.ScreenshotAsync(Number(Monitor), ct);
        Screenshot = ImageSource.FromStream(() => new MemoryStream(bytes, writable: false));
    });

    [RelayCommand]
    private Task AccountAsync() => RunAsync(async ct =>
    {
        await SaveProfileAsync(ct);
        var account = await client.CodexStatusAsync(ct);
        AccountStatus = account.SignedIn ? $"Signed in · {account.Account}" : "Not signed in to ChatGPT";
    });
    [RelayCommand] private Task LoginAsync() => RunAsync(async ct => { await SaveProfileAsync(ct); await client.LoginAsync(ct); AccountStatus = "Complete sign-in in the browser, then refresh status."; });
    [RelayCommand] private Task LogoutAsync() => RunAsync(async ct => { await client.LogoutAsync(ct); AccountStatus = "Signed out"; });

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    var tasks = await client.TasksAsync(ct);
                    var selected = tasks.FirstOrDefault(task => task.Id == (SelectedTask?.Id ?? displayedTask)) ?? tasks.FirstOrDefault(task => task.IsActive) ?? tasks.FirstOrDefault();
                    byte[]? image = null;
                    if (selected is not null && (selected.Id != displayedTask || selected.ScreenshotSequence != displayedScreenshotStep) && selected.State != TaskState.Queued)
                    {
                        try { image = await client.TaskScreenshotAsync(selected.Id, ct); }
                        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound) { }
                    }
                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        Tasks.Clear();
                        foreach (var task in tasks) Tasks.Add(task);
                        SelectedTask = selected;
                        IsTaskActive = tasks.Any(task => task.IsActive);
                        if (selected is null) return;
                        var taskChanged = selected.Id != displayedTask || selected.Version != displayedVersion;
                        displayedTask = selected.Id;
                        displayedVersion = selected.Version;
                        if (image is not null)
                        {
                            displayedScreenshotStep = selected.ScreenshotSequence;
                            Screenshot = ImageSource.FromStream(() => new MemoryStream(image, writable: false));
                        }
                        TaskStatus = $"{selected.State} · {selected.Steps.Length}/{selected.MaxSteps} steps · {selected.DoneReason}";
                        HasApproval = selected.PendingApproval is not null;
                        Preview = HasApproval ? selected.Steps.Last().PreparedAction.Preview : "No action is awaiting approval.";
                        if (taskChanged) { Steps.Clear(); foreach (var step in selected.Steps.Reverse()) Steps.Add(step); }
                        if (selected.Error is not null) Message = selected.Error;
                        if (selected.PendingApproval is { } pending && pending.Token != lastApproval)
                        {
                            lastApproval = pending.Token;
                            ApprovalRequested?.Invoke();
                        }
                    });
                }
                catch (Exception error) when (error is HttpRequestException or IOException or System.Text.Json.JsonException)
                { await MainThread.InvokeOnMainThreadAsync(() => { Status = "Backend unavailable"; Message = error.Message; }); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private void ApplyProfiles(ProfileCollection profiles)
    {
        collection = profiles;
        Profiles.Clear();
        foreach (var profile in profiles.Profiles) Profiles.Add(profile);
        SelectedProfile = profiles.Active;
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (!await operations.WaitAsync(0)) return;
        IsBusy = true;
        try { await action(lifetime.Token); }
        catch (OperationCanceledException) { Message = "Operation cancelled."; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or HttpRequestException or FormatException or System.Text.Json.JsonException)
        { Message = error.Message; }
        finally { IsBusy = false; operations.Release(); }
    }
    private static int Number(string value) => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    public void StopPolling() => lifetime.Cancel();
}
