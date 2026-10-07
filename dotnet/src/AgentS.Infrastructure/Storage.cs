using System.Text.Json;
using AgentS.Core;

namespace AgentS.Infrastructure;

public interface ISecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public static class StatePaths
{
    public static string DirectoryPath => Environment.GetEnvironmentVariable("AGENT_S_DOTNET_STATE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentS.NET");

    public static async Task AtomicWriteAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed class JsonProfileStore(string path, ISecretProtector protector) : IProfileStore, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ProfileCollection> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(path))
            {
                var id = Guid.NewGuid();
                var initial = new ProfileCollection(id, [new(id, "Default", new())]);
                await StatePaths.AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(initial, AgentJson.Options), cancellationToken);
                return initial;
            }
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var collection = JsonSerializer.Deserialize<ProfileCollection>(bytes, AgentJson.Options) ?? throw new InvalidDataException("Empty profile store.");
            return collection with { Profiles = collection.Profiles.Select(profile => Transform(profile, protector.Unprotect)).ToArray() };
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(ProfileCollection profiles, CancellationToken cancellationToken = default)
    {
        if (profiles.Profiles.Count is < 1 or > 50 || !profiles.Profiles.Any(profile => profile.Id == profiles.ActiveProfileId) ||
            profiles.Profiles.Select(profile => profile.Id).Distinct().Count() != profiles.Profiles.Count)
            throw new ArgumentException("Invalid profile collection.");
        foreach (var profile in profiles.Profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 64) throw new ArgumentException("Profile names must be 1–64 characters.");
            profile.Settings.Validate();
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var encrypted = profiles with { Profiles = profiles.Profiles.Select(profile => Transform(profile, protector.Protect)).ToArray() };
            await StatePaths.AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(encrypted, AgentJson.Options), cancellationToken);
        }
        finally { gate.Release(); }
    }

    private static Profile Transform(Profile profile, Func<string, string> transform) => profile with
    {
        Settings = profile.Settings with
        {
            Planner = profile.Settings.Planner with { ApiKey = transform(profile.Settings.Planner.ApiKey) },
            Grounding = profile.Settings.Grounding with { ApiKey = transform(profile.Settings.Grounding.ApiKey) }
        }
    };

    public void Dispose() => gate.Dispose();
}

public sealed class JsonTaskArchive(string directory) : ITaskArchive
{
    public async Task SaveAsync(TaskSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await StatePaths.AtomicWriteAsync(Path.Combine(directory, snapshot.Id.ToString("N") + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(snapshot, AgentJson.Options), cancellationToken);
        if (snapshot.Screenshot is not null)
            await StatePaths.AtomicWriteAsync(Path.Combine(directory, snapshot.Id.ToString("N") + ".png"), snapshot.Screenshot, cancellationToken);
        foreach (var old in new DirectoryInfo(directory).GetFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Skip(25))
        {
            old.Delete();
            var png = Path.ChangeExtension(old.FullName, ".png");
            if (File.Exists(png)) File.Delete(png);
        }
    }

    public async Task<IReadOnlyList<TaskSnapshot>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(directory)) return [];
        var tasks = new List<TaskSnapshot>();
        foreach (var file in new DirectoryInfo(directory).GetFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Take(25))
        {
            var task = JsonSerializer.Deserialize<TaskSnapshot>(await File.ReadAllBytesAsync(file.FullName, cancellationToken), AgentJson.Options)
                ?? throw new InvalidDataException($"Empty task archive: {file.Name}");
            var screenshotPath = Path.ChangeExtension(file.FullName, ".png");
            tasks.Add(File.Exists(screenshotPath)
                ? task with { Screenshot = await File.ReadAllBytesAsync(screenshotPath, cancellationToken) }
                : task);
        }
        return tasks;
    }
}
