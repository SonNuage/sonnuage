using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Localify.Mobile;

public sealed class App : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(new MainPage()) { Title = "Localify" };
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
