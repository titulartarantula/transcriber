using Transcriber.Core;
using Transcriber.Core.Audio;
using Transcriber.Core.Output;
using Transcriber.Core.Settings;

namespace Transcriber.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // Shared core hooks: Keystore-backed secrets, folders picked through Android's storage picker, and
        // Android's decoders for the phone's AAC recordings when voices are separated on the phone.
        Secret.Protector = new KeystoreProtector();
        AudioConvert.ExternalDecoder = path => new MediaCodecReader(path);
        ProductInfo.Client = $"Transcriber for Android {BuildInfo.From(typeof(MauiProgram).Assembly).Version}";
        NoteDestinations.FolderFactory = uri => new PhoneFolderDestination(uri);
        TextFieldColors.Register();

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
