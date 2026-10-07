namespace AgentS.Core;

public sealed record MonitorInfo(int Id, int Left, int Top, int Width, int Height, string Name);
public sealed record Screenshot(byte[] Png, MonitorInfo Monitor, DateTimeOffset CapturedAt, long ForegroundWindow);
public sealed record OcrWord(string Text, int Left, int Top, int Width, int Height);

public interface IDesktop
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    Task<Screenshot> CaptureAsync(int monitor, CancellationToken cancellationToken);
    Task<IReadOnlyList<OcrWord>> RecognizeTextAsync(Screenshot screenshot, CancellationToken cancellationToken);
    Task ExecuteAsync(PreparedAction action, Screenshot observation, CancellationToken cancellationToken);
    IDisposable AcquireLease();
}

public interface ICodeExecutor
{
    Task<CodeExecutionResult> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record CodeExecutionResult(int ExitCode, string Output, string Error);

public interface IProfileStore
{
    Task<ProfileCollection> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ProfileCollection profiles, CancellationToken cancellationToken = default);
}

public interface ITaskArchive
{
    Task SaveAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskSnapshot>> LoadAsync(CancellationToken cancellationToken = default);
}
