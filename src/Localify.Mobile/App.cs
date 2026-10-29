using Localify.Mobile.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Localify.Mobile;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Plantage lors de la session précédente : on le montre avant de relancer l'interface.
        Page page = CrashLog.ReadPending() is { } report ? new CrashPage(report, () => new MainPage()) : new MainPage();
        return new Window(page) { Title = "Localify" };
    }
}

public sealed class MainPage : ContentPage
{
    public MainPage()
    {
        BackgroundColor = Color.FromArgb("#121212");
        var view = new BlazorWebView { HostPage = "wwwroot/index.html" };
        view.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Localify.UI.App) });
        Content = view;
    }
}
