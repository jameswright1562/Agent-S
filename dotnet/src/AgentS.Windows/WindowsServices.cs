using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgentS.Core;
using AgentS.Infrastructure;

namespace AgentS.Windows;

public sealed class WindowsSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentS.NET.v1");
    public string Protect(string value) => string.IsNullOrEmpty(value) ? "" : "dpapi:" + Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    public string Unprotect(string value) => string.IsNullOrEmpty(value) ? "" : value.StartsWith("dpapi:", StringComparison.Ordinal)
        ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value[6..]), Entropy, DataProtectionScope.CurrentUser))
        : throw new InvalidDataException("Profile credentials must be DPAPI encrypted.");
}

public sealed class PowerShellExecutor : ICodeExecutor
{
    public async Task<CodeExecutionResult> ExecuteAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (script.Length > 65536) throw new ArgumentException("Script exceeds limits.");
        var info = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("PowerShell 7 is not installed.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.StandardInput.WriteLineAsync(("$ErrorActionPreference = 'Stop'\n" + script).AsMemory(), deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            return new(process.ExitCode, await output, await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            try { await Task.WhenAll(output, error); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancellationToken)) > 0)
            if (result.Length < 32768) result.Append(buffer, 0, Math.Min(read, 32768 - result.Length));
        return result.ToString();
    }
}
