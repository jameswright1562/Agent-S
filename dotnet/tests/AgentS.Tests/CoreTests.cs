using System.Text.Json;
using AgentS.Core;
using AgentS.Infrastructure;

namespace AgentS.Tests;

public sealed class CoreTests
{
    [Fact]
    public void MessagesPreserveContentOrderAndTrimOnlyOldImages()
    {
        var conversation = new Conversation("system");
        conversation.Add(LmmMessage.Observation("first", [1]));
        conversation.Add(LmmMessage.Text(MessageRole.Assistant, "history"));
        conversation.Add(LmmMessage.Observation("last", [2], textLast: true));
        conversation.RetainRecentImages(1);
        Assert.Empty(conversation.Messages[1].Content.OfType<ImageContent>());
        Assert.Equal("first", conversation.Messages[1].TextValue);
        Assert.IsType<ImageContent>(conversation.Messages[^1].Content[0]);
        Assert.IsType<TextContent>(conversation.Messages[^1].Content[1]);
        var (instructions, inputs) = CodexProvider.ConvertMessages(conversation.Messages);
        Assert.Equal("system", instructions);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(inputs));
        Assert.Contains(wire.RootElement.EnumerateArray(), item => item.TryGetProperty("text", out var text) && text.GetString() == "[assistant]");
    }

    [Theory]
    [InlineData("{\"analysis\":\"ok\",\"action\":{\"kind\":\"wait\",\"seconds\":31}}")]
    [InlineData("{\"analysis\":\"ok\",\"action\":{\"kind\":\"click\",\"elementDescription\":\"\"}}")]
    [InlineData("{\"analysis\":\"ok\",\"action\":{\"kind\":\"callCodeAgent\"}}")]
    [InlineData("{\"analysis\":\"ok\",\"action\":{\"kind\":\"done\",\"injectedField\":true}}")]
    [InlineData("{\"analysis\":\"ok\",\"action\":{\"kind\":999}}")]
    [InlineData("agent.click('target'); evil()")]
    [InlineData("{\"analysis\":\"ok\",\"action\":{\"kind\":\"done\",\"kind\":\"click\"}}")]
    [InlineData("{\"analysis\":\"ok\",\"action\":{\"kind\":\"hotkey\",\"keys\":[null]}}")]
    public void InvalidActionsNeverBecomeExecutableCommands(string response)
    {
        Assert.ThrowsAny<Exception>(() => ActionParser.Parse(response, new()));
    }

    [Fact]
    public void ManualTypingRequiresGrounding()
    {
        var action = new AgentAction { Kind = ActionKind.Type, Text = "hello" };
        Assert.Throws<ArgumentException>(() => action.Validate(new() { Approval = ApprovalMode.Manual }));
        action.Validate(new() { Approval = ApprovalMode.Auto });
    }

    [Theory]
    [InlineData("(960, 540)", 1920, 1080)]
    [InlineData("960,540", 1920, 1080)]
    public void CoordinatesScaleFromModelToPhysicalPixels(string response, int x, int y)
    {
        Assert.Equal(new(x, y), GroundingService.ParseCoordinates(response, 3840, 2160, 1920, 1080));
    }

    [Theory]
    [InlineData("(-1, 10)")]
    [InlineData("(1920, 1080)")]
    [InlineData("Reason 42: (1, 2)")]
    [InlineData("(1, 2) (3, 4)")]
    public void InvalidCoordinatesAreRejected(string response) => Assert.Throws<ArgumentException>(() => GroundingService.ParseCoordinates(response, 1920, 1080, 1920, 1080));

    [Fact]
    public void EndpointValidationRejectsEmbeddedCredentialsAndInvalidRanges()
    {
        Assert.Throws<ArgumentException>(() => new ProviderSettings { BaseUrl = "https://user:password@example.com/v1" }.Validate());
        Assert.Throws<ArgumentException>(() => new ProviderSettings { Temperature = double.NaN }.Validate());
        Assert.Throws<ArgumentException>(() => new AgentSettings { MaxSteps = 0 }.Validate());
    }

    [Fact]
    public void ValidFencedJsonActionIsParsed()
    {
        var decision = ActionParser.Parse("```json\n" + FakeProvider.Decision(ActionKind.Done) + "\n```", new());
        Assert.Equal(ActionKind.Done, decision.Action.Kind);
    }
}
