namespace AgentS.Maui.WinUI;

public partial class App : Microsoft.Maui.MauiWinUIApplication
{
    public App()
    {
        UnhandledException += (_, args) => RecordFailure(args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) RecordFailure(error);
        };
        InitializeComponent();
    }
    private static void RecordFailure(Exception error)
    {
        var directory = AgentS.Infrastructure.StatePaths.DirectoryPath;
        Directory.CreateDirectory(directory);
        File.AppendAllText(Path.Combine(directory, "startup-errors.log"), $"{DateTimeOffset.UtcNow:O}\n{error}\n");
    }
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
