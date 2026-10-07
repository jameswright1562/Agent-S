using System.Text.Json;

namespace AgentS.Core;

public sealed record CodeDecision(string Status, string Script, string Summary);
public sealed record CodeStep(int Number, string Script, CodeExecutionResult Result);
public sealed record CodeAgentResult(string Reason, string Summary, IReadOnlyList<CodeStep> Steps);

public sealed class CodeAgent(ILmmProvider provider, ICodeExecutor executor)
{
    public async Task<CodeAgentResult> RunAsync(string instruction, Screenshot screenshot, AgentSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.EnableCodeAgent) throw new InvalidOperationException("Code execution is disabled.");
        var conversation = new Conversation(Prompts.CodeAgent);
        conversation.Add(LmmMessage.Observation($"Task: {instruction}\nBudget: {settings.CodeAgentBudget} steps", screenshot.Png));
        var steps = new List<CodeStep>();
        for (int step = 1; step <= settings.CodeAgentBudget; step++)
        {
            var response = await provider.GenerateAsync(settings.Planner, conversation.Messages, cancellationToken);
            var decision = JsonSerializer.Deserialize<CodeDecision>(response, AgentJson.Options)
                ?? throw new InvalidOperationException("Code agent returned an empty response.");
            if (decision.Status is "done" or "fail") return new(decision.Status, decision.Summary, steps.ToArray());
            if (decision.Status != "execute" || string.IsNullOrWhiteSpace(decision.Script) || decision.Script.Length > 65536)
                throw new InvalidOperationException("Code agent returned an invalid script decision.");
            var result = await executor.ExecuteAsync(decision.Script, TimeSpan.FromSeconds(settings.ActionTimeoutSeconds), cancellationToken);
            steps.Add(new(step, decision.Script, result));
            conversation.Add(LmmMessage.Text(MessageRole.Assistant, response));
            conversation.Add(LmmMessage.Text(MessageRole.User, JsonSerializer.Serialize(result, AgentJson.Options)));
        }
        return new("budgetExhausted", "The coding step budget was exhausted; verify progress through the GUI.", steps.ToArray());
    }
}
