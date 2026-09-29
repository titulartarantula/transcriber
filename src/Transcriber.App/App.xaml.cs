using System.Windows;
using System.Windows.Threading;
using Transcriber.Core;
using Transcriber.Core.Audio;
using Wpf.Ui.Appearance;

namespace Transcriber.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ProductInfo.Client = $"Transcriber for Windows {BuildInfo.From(typeof(App).Assembly).Version}";
        // Media Foundation decodes mp3, m4a and the like for "Transcribe a file…".
        AudioConvert.ExternalDecoder = path => new NAudio.Wave.AudioFileReader(path);
        AudioConvert.Encoder = Transcriber.Audio.Windows.AacEncoder.Encode;
        ApplicationThemeManager.ApplySystemTheme();
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Local);
            File.AppendAllText(Path.Combine(AppPaths.Local, "error.log"), $"[{DateTime.Now:O}] {e.Exception}\n\n");
        }
        catch (IOException)
        {
        }

        MessageBox.Show(e.Exception.Message, "Transcriber", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
