using System.Collections.ObjectModel;
using AgentS.Core;
using AgentS.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AgentS.Maui.ViewModels;

public partial class ProviderEditorViewModel : ObservableObject
{
    private bool loading;
    private ProviderSettings original = new();
    public IReadOnlyList<ProviderPreset> Presets => ProviderCatalog.Presets;
    public ObservableCollection<string> Models { get; } = [];
    [ObservableProperty] public partial ProviderPreset? SelectedPreset { get; set; }
    [ObservableProperty] public partial string Model { get; set; }
    [ObservableProperty] public partial string? SelectedModel { get; set; }
    [ObservableProperty] public partial string BaseUrl { get; set; }
    [ObservableProperty] public partial string ApiKey { get; set; }
    [ObservableProperty] public partial string ApiVersion { get; set; }
    [ObservableProperty] public partial string RuntimePath { get; set; }
    [ObservableProperty] public partial string ReasoningEffort { get; set; }
    [ObservableProperty] public partial bool ClearKey { get; set; }
    public IReadOnlyList<string> Efforts { get; } = ["low", "medium", "high", "minimal", "none", "xhigh"];
    public bool IsCodex => SelectedPreset?.Kind == ProviderKind.Codex;
    public bool UsesHttp => !IsCodex;
    public bool IsAzure => SelectedPreset?.Kind == ProviderKind.Azure;
    public ProviderEditorViewModel()
    {
        Model = BaseUrl = ApiKey = "";
        ApiVersion = "2024-10-21";
        RuntimePath = "codex";
        ReasoningEffort = "low";
    }

    partial void OnSelectedPresetChanged(ProviderPreset? value)
    {
        OnPropertyChanged(nameof(IsCodex));
        OnPropertyChanged(nameof(UsesHttp));
        OnPropertyChanged(nameof(IsAzure));
        if (loading || value is null) return;
        BaseUrl = value.BaseUrl;
        ApiKey = "";
        Model = value.Kind == ProviderKind.Codex ? "gpt-5.6-luna" : "";
        Models.Clear();
    }

    partial void OnSelectedModelChanged(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) Model = value;
    }

    public void Load(ProviderSettings settings)
    {
        loading = true;
        try
        {
            original = settings;
            SelectedPreset = Presets.Single(preset => preset.Kind == settings.Kind);
            Model = settings.Model;
            BaseUrl = settings.BaseUrl;
            ApiKey = "";
            ApiVersion = settings.ApiVersion;
            RuntimePath = settings.CodexRuntimePath;
            ReasoningEffort = settings.ReasoningEffort;
            ClearKey = false;
            Models.Clear();
            SelectedModel = null;
        }
        finally { loading = false; }
    }

    public ProviderSettings Build() => original with
    {
        Kind = SelectedPreset?.Kind ?? ProviderKind.LmStudio, Model = Model?.Trim() ?? "", BaseUrl = (BaseUrl ?? "").Trim().TrimEnd('/'),
        ApiKey = ApiKey ?? "", ApiVersion = ApiVersion?.Trim() ?? "", CodexRuntimePath = RuntimePath?.Trim() ?? "", ReasoningEffort = ReasoningEffort ?? "low"
    };
}
