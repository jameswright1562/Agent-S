using System.Text.Json;

namespace AgentS.Core;

public sealed record BehaviorSummary(Guid TaskId, string Narrative);
public sealed record BehaviorChoice(int Candidate, string Reason);

public sealed class BehaviorSelector(ILmmProvider provider)
{
    public async Task<BehaviorSummary> NarrateAsync(TaskSnapshot trajectory, ProviderSettings settings, CancellationToken cancellationToken)
    {
        var trajectoryText = string.Join('\n', trajectory.Steps.Select(step => $"Step {step.Number}: {step.Analysis}\n{step.PreparedAction.Preview}\n{step.State}"));
        var narrative = await provider.GenerateAsync(settings,
            [LmmMessage.Text(MessageRole.System, "Describe the observed behavior of this computer-use trajectory. Distinguish verified outcomes from claims. Be concise."),
             LmmMessage.Text(MessageRole.User, $"Task: {trajectory.Instruction}\n{trajectoryText}")], cancellationToken);
        return new(trajectory.Id, narrative);
    }

    public async Task<BehaviorChoice> ChooseAsync(string instruction, IReadOnlyList<BehaviorSummary> candidates, ProviderSettings settings, CancellationToken cancellationToken)
    {
        if (candidates.Count < 2) throw new ArgumentException("Provide at least two candidate trajectories.");
        var response = await provider.GenerateAsync(settings,
            [LmmMessage.Text(MessageRole.System, "Compare candidate computer-use behaviors against the exact task. Prefer verified success and faithful execution. Return JSON {\"candidate\": zero-based index, \"reason\": explanation}."),
             LmmMessage.Text(MessageRole.User, $"Task: {instruction}\n{JsonSerializer.Serialize(candidates, AgentJson.Options)}")], cancellationToken);
        var choice = JsonSerializer.Deserialize<BehaviorChoice>(response, AgentJson.Options) ?? throw new InvalidOperationException("No behavior choice.");
        if (choice.Candidate < 0 || choice.Candidate >= candidates.Count) throw new InvalidOperationException("Behavior judge returned an invalid candidate index.");
        return choice;
    }
}
