using System.Runtime.InteropServices;
using AgentS.Maui.ViewModels;
using Microsoft.UI.Windowing;

namespace AgentS.Maui.Views;

public partial class MainPage : ContentPage
{
    private readonly MainViewModel viewModel;
    private bool connected;
    public MainPage(MainViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;
        viewModel.PrepareDesktop = PrepareDesktop;
        viewModel.ApprovalRequested = ShowApproval;
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (connected) return;
        connected = true;
        await viewModel.ConnectCommand.ExecuteAsync(null);
    }
    private void PrepareDesktop()
    {
        AllowSetForegroundWindow(uint.MaxValue);
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native && native.AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Minimize();
    }
    private void ShowApproval()
    {
        if (Window?.Handler?.PlatformView is Microsoft.UI.Xaml.Window native && native.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Restore();
            native.Activate();
        }
    }
    public void StopPolling() => viewModel.StopPolling();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AllowSetForegroundWindow(uint processId);
}
