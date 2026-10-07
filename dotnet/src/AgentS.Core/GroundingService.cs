using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentS.Core;

public sealed partial class GroundingService(ILmmProvider provider, IDesktop desktop)
{
    public async Task<PreparedAction> PrepareAsync(AgentAction action, Screenshot screenshot, AgentSettings settings, CancellationToken cancellationToken)
    {
        action.Validate(settings);
        if (action.Kind is ActionKind.Click or ActionKind.Scroll || action.Kind == ActionKind.Type && action.ElementDescription is not null)
            return new(action, Point: await PointAsync(action.ElementDescription!, screenshot, settings, cancellationToken));
        if (action.Kind == ActionKind.DragAndDrop)
            return new(action,
                Start: await PointAsync(action.StartingDescription!, screenshot, settings, cancellationToken),
                End: await PointAsync(action.EndingDescription!, screenshot, settings, cancellationToken));
        if (action.Kind == ActionKind.HighlightTextSpan)
        {
            var words = await desktop.RecognizeTextAsync(screenshot, cancellationToken);
            return new(action,
                Start: await TextPointAsync(action.StartingDescription!, words, screenshot, settings, false, cancellationToken),
                End: await TextPointAsync(action.EndingDescription!, words, screenshot, settings, true, cancellationToken));
        }
        return new(action);
    }

    private async Task<ScreenPoint> PointAsync(string description, Screenshot screenshot, AgentSettings settings, CancellationToken cancellationToken)
    {
        var response = await provider.GenerateAsync(settings.Grounding,
            [LmmMessage.Observation($"Query:{description}\nOutput only the coordinate of one point as (x, y).", screenshot.Png, textLast: true)], cancellationToken);
        return ParseCoordinates(response, screenshot.Monitor.Width, screenshot.Monitor.Height, settings.GroundingWidth, settings.GroundingHeight);
    }

    public static ScreenPoint ParseCoordinates(string response, int width, int height, int modelWidth, int modelHeight)
    {
        var match = CoordinatePair().Match(response.Trim());
        if (!match.Success) throw new ArgumentException("Grounding must return one coordinate pair.");
        var x = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var y = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (modelWidth <= 0 || modelHeight <= 0 || width <= 0 || height <= 0 || x < 0 || y < 0 || x >= modelWidth || y >= modelHeight)
            throw new ArgumentException("Grounded point is outside the model image.");
        var point = new ScreenPoint((int)Math.Round(x * width / modelWidth), (int)Math.Round(y * height / modelHeight));
        if (point.X >= width || point.Y >= height) throw new ArgumentException("Grounded point is outside the selected monitor.");
        return point;
    }

    private async Task<ScreenPoint> TextPointAsync(string phrase, IReadOnlyList<OcrWord> words, Screenshot screenshot, AgentSettings settings, bool end, CancellationToken cancellationToken)
    {
        if (words.Count == 0) throw new InvalidOperationException("No text was detected in the screenshot.");
        var table = string.Join('\n', words.Select((word, index) => $"{index}\t{word.Text}"));
        var prompt = $"Find the {(end ? "LAST" : "FIRST")} word of the phrase '{phrase}' in this OCR table. Return only its integer ID.\n{table}";
        var response = await provider.GenerateAsync(settings.Planner, [LmmMessage.Observation(prompt, screenshot.Png)], cancellationToken);
        if (!int.TryParse(response.Trim(), CultureInfo.InvariantCulture, out var id) || id < 0 || id >= words.Count)
            throw new InvalidOperationException("Text grounding returned an invalid word ID.");
        var word = words[id];
        return new(end ? word.Left + word.Width - 1 : word.Left, word.Top + word.Height / 2);
    }

    [GeneratedRegex(@"\A\s*\(?\s*(-?\d+(?:\.\d+)?)\s*,\s*(-?\d+(?:\.\d+)?)\s*\)?\s*\z", RegexOptions.CultureInvariant)]
    private static partial Regex CoordinatePair();
}
