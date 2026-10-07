namespace AgentS.Core;

public sealed record PlanResult(PlannerDecision Decision, string Reflection, string RawResponse);

public sealed class S3Planner
{
    private readonly ILmmProvider provider;
    private readonly AgentSettings settings;
    private readonly Conversation conversation;
    private readonly Conversation reflector;
    private readonly List<string> notes = [];
    private string previousAction = "No action has been taken yet.";
    private int turn;

    public S3Planner(ILmmProvider provider, AgentSettings settings, string instruction)
    {
        this.provider = provider;
        this.settings = settings;
        conversation = new(Prompts.Planner(instruction, settings.EnableCodeAgent));
        reflector = new(Prompts.Reflection + "\nTask: " + instruction);
    }

    public async Task<PlanResult> PlanAsync(Screenshot screenshot, CancellationToken cancellationToken)
    {
        string reflection = "";
        if (settings.EnableReflection)
        {
            reflector.Add(LmmMessage.Observation(previousAction, screenshot.Png));
            reflector.RetainRecentImages(settings.HistoryImages);
            if (turn > 0)
                reflection = await provider.GenerateAsync(settings.Planner, reflector.Messages, cancellationToken);
        }
        conversation.Add(LmmMessage.Observation($"{previousAction}\nReflection: {reflection}\nCurrent text buffer: {string.Join('\n', notes)}", screenshot.Png));
        conversation.RetainRecentImages(settings.HistoryImages);
        for (int attempt = 0; ; attempt++)
        {
            var response = await provider.GenerateAsync(settings.Planner, conversation.Messages, cancellationToken);
            try
            {
                var decision = ActionParser.Parse(response, settings);
                conversation.Add(LmmMessage.Text(MessageRole.Assistant, response));
                turn++;
                return new(decision, reflection, response);
            }
            catch (Exception error) when (attempt == 0 && error is ArgumentException or System.Text.Json.JsonException)
            {
                conversation.Add(LmmMessage.Text(MessageRole.Assistant, response));
                conversation.Add(LmmMessage.Text(MessageRole.User, $"Invalid action format: {error.Message}. Return one valid JSON decision using only the documented action schema."));
            }
        }
    }

    public void ObserveResult(PreparedAction action, string result)
    {
        previousAction = $"Previous action: {action.Preview}\nResult: {result}";
        if (action.Action.Kind == ActionKind.SaveToKnowledge) notes.AddRange(action.Action.Notes);
    }
}
