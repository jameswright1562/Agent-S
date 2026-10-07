using AgentS.Core;

namespace AgentS.Infrastructure;

public sealed record ProviderPreset(ProviderKind Kind, string Name, string BaseUrl, string KeyEnvironment);

public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderPreset> Presets { get; } = [
        new(ProviderKind.LmStudio, "LM Studio", "http://localhost:1234/v1", ""),
        new(ProviderKind.OpenAICompatible, "OpenAI-compatible", "http://localhost:1234/v1", "OPENAI_API_KEY"),
        new(ProviderKind.Ollama, "Ollama", "http://localhost:11434/v1", ""),
        new(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "OPENAI_API_KEY"),
        new(ProviderKind.Anthropic, "Anthropic", "https://api.anthropic.com/v1", "ANTHROPIC_API_KEY"),
        new(ProviderKind.Gemini, "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "GEMINI_API_KEY"),
        new(ProviderKind.OpenRouter, "OpenRouter", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY"),
        new(ProviderKind.DeepSeek, "DeepSeek", "https://api.deepseek.com/v1", "DEEPSEEK_API_KEY"),
        new(ProviderKind.Qwen, "Qwen", "https://dashscope.aliyuncs.com/compatible-mode/v1", "QWEN_API_KEY"),
        new(ProviderKind.Parasail, "Parasail", "https://api.parasail.io/v1", "PARASAIL_API_KEY"),
        new(ProviderKind.Azure, "Azure OpenAI", "", "AZURE_OPENAI_API_KEY"),
        new(ProviderKind.HuggingFace, "Hugging Face endpoint", "", "HF_TOKEN"),
        new(ProviderKind.Vllm, "vLLM", "http://localhost:8001/v1", "vLLM_API_KEY"),
        new(ProviderKind.Codex, "Codex · ChatGPT account", "", "")
    ];

    public static string ResolveApiKey(ProviderSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ApiKey)) return settings.ApiKey;
        var preset = Presets.Single(item => item.Kind == settings.Kind);
        var key = string.IsNullOrEmpty(preset.KeyEnvironment) ? null : Environment.GetEnvironmentVariable(preset.KeyEnvironment);
        if (!string.IsNullOrEmpty(key)) return key;
        if (Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var url) && url.IsLoopback) return "local";
        throw new ArgumentException($"Configure an API key for {preset.Name}.");
    }
}
