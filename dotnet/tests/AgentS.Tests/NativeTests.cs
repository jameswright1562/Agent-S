using AgentS.Core;
using AgentS.Windows;

namespace AgentS.Tests;

public sealed class NativeTests
{
    [Fact]
    public async Task NativeOcrReturnsWordBoxesForGeneratedImage()
    {
        using var bitmap = new System.Drawing.Bitmap(640, 100);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
        using (var font = new System.Drawing.Font("Segoe UI", 32))
        {
            graphics.Clear(System.Drawing.Color.White);
            graphics.DrawString("HELLO WORLD", font, System.Drawing.Brushes.Black, 10, 10);
        }
        using var output = new MemoryStream();
        bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
        var screenshot = new Screenshot(output.ToArray(), new(1, 0, 0, 640, 100, "generated"), DateTimeOffset.UtcNow, 0);
        var words = await new WindowsDesktop().RecognizeTextAsync(screenshot, CancellationToken.None);
        Assert.Contains(words, word => word.Text.Equals("HELLO", StringComparison.OrdinalIgnoreCase));
        Assert.All(words, word => Assert.True(word.Left >= 0 && word.Top >= 0 && word.Width > 0 && word.Height > 0));
    }
    [Fact]
    public void NativeDesktopLeaseExcludesAnotherOwnerAndReleases()
    {
        var first = new WindowsDesktop();
        var second = new WindowsDesktop();
        using (first.AcquireLease()) Assert.Throws<TaskConflictException>(() => second.AcquireLease());
        using var released = second.AcquireLease();
    }

    [Fact]
    public async Task NativePowerShellIsStandaloneAndReturnsResults()
    {
        var result = await new PowerShellExecutor().ExecuteAsync("Write-Output 'native-dotnet-test'", TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("native-dotnet-test", result.Output);
    }

    [Fact]
    public async Task NativePowerShellCancellationStopsProcessTree()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PowerShellExecutor().ExecuteAsync("Start-Sleep -Seconds 30", TimeSpan.FromSeconds(60), timeout.Token));
    }
}
