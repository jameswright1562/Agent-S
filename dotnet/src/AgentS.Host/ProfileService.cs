using AgentS.Core;
using AgentS.Infrastructure;

public sealed record LaunchRequest(string Instruction, Guid? ProfileId = null, ApprovalMode? Approval = null, int? MaxSteps = null);
public sealed record ApprovalRequest(int Step, Guid Token, bool Approved);
public sealed record ConnectionRequest(bool Grounding = false, ProviderSettings? Settings = null);
public sealed record ProfileUpdate(ProfileCollection Collection, bool ClearPlannerKey = false, bool ClearGroundingKey = false);

public sealed class ProfileService(IProfileStore store) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public Task<ProfileCollection> LoadAsync(CancellationToken cancellationToken) => store.LoadAsync(cancellationToken);

    public async Task<ProfileCollection> UpdateAsync(ProfileUpdate request, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var current = await store.LoadAsync(cancellationToken);
            var collection = request.Collection with { Profiles = request.Collection.Profiles.Select(profile =>
            {
                var previous = current.Profiles.SingleOrDefault(item => item.Id == profile.Id);
                return profile with { Settings = profile.Settings with
                {
                    Planner = profile.Settings.Planner with { ApiKey = request.ClearPlannerKey && profile.Id == request.Collection.ActiveProfileId ? "" :
                        string.IsNullOrEmpty(profile.Settings.Planner.ApiKey) && previous?.Settings.Planner.Kind == profile.Settings.Planner.Kind && previous.Settings.Planner.BaseUrl == profile.Settings.Planner.BaseUrl
                            ? previous.Settings.Planner.ApiKey : profile.Settings.Planner.ApiKey },
                    Grounding = profile.Settings.Grounding with { ApiKey = request.ClearGroundingKey && profile.Id == request.Collection.ActiveProfileId ? "" :
                        string.IsNullOrEmpty(profile.Settings.Grounding.ApiKey) && previous?.Settings.Grounding.Kind == profile.Settings.Grounding.Kind && previous.Settings.Grounding.BaseUrl == profile.Settings.Grounding.BaseUrl
                            ? previous.Settings.Grounding.ApiKey : profile.Settings.Grounding.ApiKey }
                } };
            }).ToArray() };
            await store.SaveAsync(collection, cancellationToken);
            return collection;
        }
        finally { gate.Release(); }
    }

    public async Task<ProviderSettings> ResolveProviderAsync(ConnectionRequest request, CancellationToken cancellationToken)
    {
        var active = (await store.LoadAsync(cancellationToken)).Active.Settings;
        var saved = request.Grounding ? active.Grounding : active.Planner;
        var settings = request.Settings ?? saved;
        // Saved secrets only follow their original endpoint/provider.
        if (string.IsNullOrEmpty(settings.ApiKey) && settings.Kind == saved.Kind && settings.BaseUrl == saved.BaseUrl)
            settings = settings with { ApiKey = saved.ApiKey };
        settings.Validate();
        return settings;
    }

    public static ProfileCollection Public(ProfileCollection collection) => collection with
    {
        Profiles = collection.Profiles.Select(profile => profile with { Settings = profile.Settings.WithoutSecrets() }).ToArray()
    };
    public void Dispose() => gate.Dispose();
}
