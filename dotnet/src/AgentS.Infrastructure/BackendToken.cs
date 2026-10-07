using System.Security.Cryptography;
using System.Text;

namespace AgentS.Infrastructure;

public static class BackendToken
{
    public static async Task<string> LoadAsync(CancellationToken cancellationToken = default)
    {
        var explicitToken = Environment.GetEnvironmentVariable("AGENT_S_DOTNET_TOKEN");
        if (!string.IsNullOrEmpty(explicitToken)) return explicitToken;
        Directory.CreateDirectory(StatePaths.DirectoryPath);
        var path = Path.Combine(StatePaths.DirectoryPath, "backend-token");
        if (!File.Exists(path))
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            try
            {
                await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(Encoding.UTF8.GetBytes(token), cancellationToken);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        return (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
    }

    public static bool Matches(string expected, string actual) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
}
