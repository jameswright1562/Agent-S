using System.Text.Json;
using AgentS.Client;
using AgentS.Core;
using AgentS.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };
if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
{
    Console.WriteLine("""
        Agent S .NET · Windows desktop agent
        Commands:
          status                       Backend status and monitor inventory
          profiles                     View profiles (secrets excluded)
          models [--grounding]         Discover models; no generation
          run "instruction" [--approval dryRun|manual|auto] [--steps 15]
          task <id>                    Inspect task state
          stop <id>                    Cancel a task
          approve <id> <step> <token>   Approve exactly one pending action
          reject <id> <step> <token>    Reject exactly one pending action
          login | logout               ChatGPT account sign-in/out
          shutdown                     Stop the shared backend
          mcp                          MCP JSON-RPC server over stdio
        Settings are managed in the MAUI app or /api/profiles.
        """);
    return;
}
try
{
    ValidateArguments(args);
    using var client = new BackendClient();
    if (args[0] == "mcp") { await new McpServer(client).RunAsync(Console.In, Console.Out, cancellation.Token); return; }
    await client.EnsureStartedAsync(cancellation.Token);
    object? result = null;
    switch (args[0])
    {
        case "status": result = await client.StatusAsync(cancellation.Token); break;
        case "profiles": result = await client.ProfilesAsync(cancellation.Token); break;
        case "models": result = await client.ModelsAsync(new(args.Contains("--grounding")), cancellation.Token); break;
        case "task": result = await client.TaskAsync(Guid.Parse(args[1]), cancellation.Token); break;
        case "stop": await client.StopAsync(Guid.Parse(args[1]), cancellation.Token); break;
        case "approve":
        case "reject": await client.ApproveAsync(Guid.Parse(args[1]), new(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), Guid.Parse(args[3]), args[0] == "approve"), cancellation.Token); break;
        case "shutdown": await client.ShutdownAsync(cancellation.Token); break;
        case "login": await client.LoginAsync(cancellation.Token); Console.WriteLine("Complete ChatGPT sign-in in the browser."); break;
        case "logout": await client.LogoutAsync(cancellation.Token); break;
        case "run":
            ApprovalMode? approval = null;
            int? steps = null;
            for (int index = 2; index < args.Length; index += 2)
            {
                if (index + 1 == args.Length) throw new ArgumentException("Missing option value.");
                if (args[index] == "--approval") approval = Enum.Parse<ApprovalMode>(args[index + 1], ignoreCase: true);
                else if (args[index] == "--steps") steps = int.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture);
                else throw new ArgumentException($"Unknown option: {args[index]}");
            }
            var task = await client.LaunchAsync(new(args[1], Approval: approval, MaxSteps: steps), cancellation.Token);
            Console.WriteLine($"Task {task.Id}. Open MAUI or another CLI session to approve manual actions.");
            try { result = await client.WaitAsync(task.Id, cancellation.Token); }
            catch (OperationCanceledException) { await client.StopAsync(task.Id, CancellationToken.None); throw; }
            break;
        default: throw new ArgumentException("Unknown command; run agent-s help.");
    }
    if (result is not null) Console.WriteLine(JsonSerializer.Serialize(result, AgentJson.Options));
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); Environment.ExitCode = 130; }
catch (Exception error) when (error is ArgumentException or HttpRequestException or IOException or InvalidOperationException or IndexOutOfRangeException or FormatException or JsonException)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}

static void ValidateArguments(string[] arguments)
{
    var valid = arguments[0] switch
    {
        "status" or "profiles" or "login" or "logout" or "shutdown" or "mcp" => arguments.Length == 1,
        "models" => arguments.Length == 1 || arguments.Length == 2 && arguments[1] == "--grounding",
        "task" or "stop" => arguments.Length == 2,
        "approve" or "reject" => arguments.Length == 4,
        "run" => arguments.Length >= 2 && arguments.Length % 2 == 0,
        _ => false
    };
    if (!valid) throw new ArgumentException("Invalid command or arguments; run agent-s help.");
}
