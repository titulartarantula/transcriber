namespace Transcriber.Mobile;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new NavigationPage(new MainPage())) { Title = "Transcriber" };
        AppLifecycle.Attach(window);
        return window;
    }
}
