using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AgentS.Core;

public enum MessageRole { System, User, Assistant }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(ImageContent), "image")]
public abstract record MessageContent;
public sealed record TextContent(string Text) : MessageContent;
public sealed record ImageContent(string Url, string Detail = "high") : MessageContent
{
    public static ImageContent FromPng(byte[] image) => new($"data:image/png;base64,{Convert.ToBase64String(image)}");
}

public sealed record LmmMessage(MessageRole Role, ImmutableArray<MessageContent> Content)
{
    public static LmmMessage Text(MessageRole role, string text) => new(role, [new TextContent(text)]);
    public static LmmMessage Observation(string text, byte[] image, bool textLast = false) =>
        new(MessageRole.User, textLast
            ? [ImageContent.FromPng(image), new TextContent(text)]
            : [new TextContent(text), ImageContent.FromPng(image)]);
    [JsonIgnore] public string TextValue => string.Join('\n', Content.OfType<TextContent>().Select(part => part.Text));
}

public sealed class Conversation(string systemPrompt)
{
    private readonly List<LmmMessage> messages = [LmmMessage.Text(MessageRole.System, systemPrompt)];
    public ImmutableArray<LmmMessage> Messages => [.. messages];
    public void Add(LmmMessage message) => messages.Add(message);
    public void RetainRecentImages(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        int retained = 0;
        for (int index = messages.Count - 1; index > 0; index--)
        {
            var content = messages[index].Content.Reverse()
                .Where(part => part is not ImageContent || ++retained <= count).Reverse().ToImmutableArray();
            messages[index] = messages[index] with { Content = content };
        }
    }
}

public interface ILmmProvider
{
    Task<string> GenerateAsync(ProviderSettings settings, IReadOnlyList<LmmMessage> messages, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListModelsAsync(ProviderSettings settings, CancellationToken cancellationToken);
}
