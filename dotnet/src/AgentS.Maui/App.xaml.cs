using AgentS.Maui.Views;

namespace AgentS.Maui;

public partial class App : Application
{
    private readonly IServiceProvider services;
    public App(IServiceProvider services) { InitializeComponent(); this.services = services; UserAppTheme = AppTheme.Dark; }
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var page = services.GetRequiredService<MainPage>();
        var display = DeviceDisplay.Current.MainDisplayInfo;
        var width = Math.Min(1480, display.Width / display.Density - 32);
        var height = Math.Min(920, display.Height / display.Density - 80);
        var window = new Window(page)
        {
            Title = "Agent S · Desktop workspace", Width = width, Height = height,
            MinimumWidth = Math.Min(1120, width), MinimumHeight = Math.Min(600, height)
        };
        window.Destroying += (_, _) => page.StopPolling();
        return window;
    }
}
