using Transcriber.Core;
using Transcriber.Core.Output;
using Transcriber.Core.Settings;

namespace Transcriber.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Shared core hooks: Keystore-backed secrets and folders picked through Android's storage picker.
        Secret.Protector = new KeystoreProtector();
        ProductInfo.Client = $"Transcriber for Android {BuildInfo.From(typeof(MauiProgram).Assembly).Version}";
        NoteDestinations.FolderFactory = uri => new PhoneFolderDestination(uri);

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });
        return builder.Build();
    }
}
