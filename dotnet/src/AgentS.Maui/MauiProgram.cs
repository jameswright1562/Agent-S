using AgentS.Client;
using AgentS.Maui.ViewModels;
using AgentS.Maui.Views;

namespace AgentS.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<BackendClient>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
