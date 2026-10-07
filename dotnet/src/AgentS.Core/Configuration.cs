using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentS.Core;

public enum ProviderKind { OpenAI, LmStudio, OpenAICompatible, Ollama, Vllm, OpenRouter, Anthropic, Gemini, HuggingFace, Azure, DeepSeek, Qwen, Parasail, Codex }
public enum ApprovalMode { DryRun, Manual, Auto }

public sealed record ProviderSettings
{
    public ProviderKind Kind { get; init; } = ProviderKind.LmStudio;
    public string Model { get; init; } = "";
    public string BaseUrl { get; init; } = "http://localhost:1234/v1";
    public string ApiKey { get; init; } = "";
    public string ApiVersion { get; init; } = "2024-10-21";
    public double Temperature { get; init; }
    public string ReasoningEffort { get; init; } = "low";
    public string CodexRuntimePath { get; init; } = "codex";
    public int TimeoutSeconds { get; init; } = 60;
    
    public void Validate(bool requireModel = false)
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Unknown provider.");
        if (requireModel && string.IsNullOrWhiteSpace(Model)) throw new ArgumentException("Configure a model ID.");
        if (Model.Length > 256 || ApiKey.Length > 4096) throw new ArgumentException("Provider settings exceed limits.");
        if (!double.IsFinite(Temperature) || Temperature is < 0 or > 2) throw new ArgumentException("Temperature must be 0–2.");
        if (TimeoutSeconds is < 5 or > 300) throw new ArgumentException("Inference timeout must be 5–300 seconds.");
        if (ReasoningEffort is not ("low" or "medium" or "high" or "minimal" or "none" or "xhigh")) throw new ArgumentException("Unknown reasoning effort.");
        if (Kind == ProviderKind.Codex)
        {
            if (string.IsNullOrWhiteSpace(CodexRuntimePath)) throw new ArgumentException("Configure the Codex runtime path.");
            return;
        }
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Endpoint must be an absolute HTTP(S) URL without credentials, query, or fragment.");
    }
}

public sealed record AgentSettings
{
    public ProviderSettings Planner { get; init; } = new();
    public ProviderSettings Grounding { get; init; } = new();
    public int GroundingWidth { get; init; } = 1920;
    public int GroundingHeight { get; init; } = 1080;
    public int MaxSteps { get; init; } = 15;
    public int HistoryImages { get; init; } = 4;
    public int Monitor { get; init; } = 1;
    public bool EnableReflection { get; init; } = true;
    public bool EnableCodeAgent { get; init; }
    public int CodeAgentBudget { get; init; } = 20;
    public ApprovalMode Approval { get; init; } = ApprovalMode.DryRun;
    public int TaskTimeoutSeconds { get; init; } = 600;
    public int ActionTimeoutSeconds { get; init; } = 60;

    public AgentSettings WithoutSecrets() => this with
    {
        Planner = Planner with { ApiKey = "" }, Grounding = Grounding with { ApiKey = "" }
    };

    public void Validate(bool requireModels = false)
    {
        Planner.Validate(requireModels);
        Grounding.Validate(requireModels);
        if (Grounding.Kind == ProviderKind.Codex) throw new ArgumentException("Use a vision grounding endpoint, not Codex, for coordinate grounding.");
        if (MaxSteps is < 1 or > 60 || HistoryImages is < 1 or > 30 || Monitor is < 1 or > 32 ||
            GroundingWidth is < 1 or > 8192 || GroundingHeight is < 1 or > 8192 ||
            TaskTimeoutSeconds is < 10 or > 3600 || ActionTimeoutSeconds is < 5 or > 300 || CodeAgentBudget is < 1 or > 30 ||
            !Enum.IsDefined(Approval)) throw new ArgumentException("Agent settings are outside their supported ranges.");
    }
}

public sealed record Profile(Guid Id, string Name, AgentSettings Settings);
public sealed record ProfileCollection(Guid ActiveProfileId, IReadOnlyList<Profile> Profiles)
{
    [JsonIgnore] public Profile Active => Profiles.Single(profile => profile.Id == ActiveProfileId);
}

public static class AgentJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
