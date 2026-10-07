using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentS.Core;

public enum ActionKind { Click, Type, Scroll, DragAndDrop, HighlightTextSpan, Hotkey, HoldAndPress, Wait, Open, SwitchApplications, SaveToKnowledge, CallCodeAgent, Done, Fail }
public enum MouseButton { Left, Middle, Right }
public readonly record struct ScreenPoint(int X, int Y);

public sealed record AgentAction
{
    public required ActionKind Kind { get; init; }
    public string? ElementDescription { get; init; }
    public string? StartingDescription { get; init; }
    public string? EndingDescription { get; init; }
    public string Text { get; init; } = "";
    public string Target { get; init; } = "";
    public string? Task { get; init; }
    public int Clicks { get; init; } = 1;
    public MouseButton Button { get; init; }
    public bool Overwrite { get; init; }
    public bool Enter { get; init; }
    public bool Horizontal { get; init; }
    public double Seconds { get; init; } = 1;
    public ImmutableArray<string> Keys { get; init; } = [];
    public ImmutableArray<string> HoldKeys { get; init; } = [];
    public ImmutableArray<string> Notes { get; init; } = [];

    public void Validate(AgentSettings settings)
    {
        if (!Enum.IsDefined(Kind) || !Enum.IsDefined(Button)) throw new ArgumentException("Unknown action or button.");
        foreach (var value in new[] { ElementDescription, StartingDescription, EndingDescription, Text, Target, Task })
            if (value?.Length > 32768) throw new ArgumentException("Action text exceeds limits.");
        if (Clicks is < -100 or > 100 || (Kind == ActionKind.Click && Clicks < 1)) throw new ArgumentException("Invalid click count.");
        if (!double.IsFinite(Seconds) || Seconds is < 0 or > 30) throw new ArgumentException("Wait must be 0–30 seconds.");
        foreach (var keys in new[] { Keys, HoldKeys })
            if (keys.Length > 32 || keys.Any(key => key is null || key.Length is < 1 or > 32 || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new ArgumentException("Invalid key names.");
        if (Kind is ActionKind.Click or ActionKind.Scroll || (Kind == ActionKind.Type && (settings.Approval == ApprovalMode.Manual || ElementDescription is not null)))
            Require(ElementDescription, "Element description");
        if (Kind is ActionKind.DragAndDrop or ActionKind.HighlightTextSpan)
        {
            Require(StartingDescription, "Start description");
            Require(EndingDescription, "End description");
        }
        if (Kind is ActionKind.Open or ActionKind.SwitchApplications) Require(Target, "Application/file target");
        if (Kind is ActionKind.Hotkey or ActionKind.HoldAndPress && Keys.IsEmpty) throw new ArgumentException("Provide keys.");
        if (Kind == ActionKind.CallCodeAgent && !settings.EnableCodeAgent) throw new ArgumentException("Code agent is disabled.");
        if (Notes.Length > 100 || Notes.Any(note => note is null || note.Length > 32768)) throw new ArgumentException("Notes exceed limits.");
    }

    private static void Require(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.");
    }
}

public sealed record PreparedAction(AgentAction Action, ScreenPoint? Point = null, ScreenPoint? Start = null, ScreenPoint? End = null)
{
    [JsonIgnore] public string Preview => JsonSerializer.Serialize(this, AgentJson.Options);
}

public sealed record PlannerDecision(string Analysis, AgentAction Action);

public static partial class ActionParser
{
    // The port uses JSON commands instead of interpreting generated Python/C#.
    public static PlannerDecision Parse(string response, AgentSettings settings)
    {
        if (response.Length > 65536) throw new ArgumentException("Planner response exceeds limits.");
        var match = JsonFence().Match(response.Trim());
        var json = match.Success ? match.Groups[1].Value : response.Trim();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateProperties(document.RootElement);
        var decision = JsonSerializer.Deserialize<PlannerDecision>(json, AgentJson.Options)
            ?? throw new ArgumentException("Planner returned no action.");
        if (decision.Action is null || decision.Analysis is null) throw new ArgumentException("Analysis and action are required.");
        decision.Action.Validate(settings);
        return decision;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate action fields are not allowed.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) RejectDuplicateProperties(child);
    }

    [GeneratedRegex(@"\A```(?:json)?\s*([\s\S]*?)\s*```\z", RegexOptions.CultureInvariant)]
    private static partial Regex JsonFence();
}
