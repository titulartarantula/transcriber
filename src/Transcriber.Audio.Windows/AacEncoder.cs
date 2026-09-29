using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Transcriber.Core.Audio;

namespace Transcriber.Audio.Windows;

/// <summary>
/// Compresses a recording to mono AAC with Windows' own encoder, for sending to the server and keeping.
/// A raw loopback capture is ~1.4 GB an hour; this is ~43 MB, and transcribes the same.
/// </summary>
public static class AacEncoder
{
    /// <summary>
    /// 48 kHz keeps everything the capture had. 96 kbps is double the phone's rate: the PC isn't short of
    /// space, and the headroom helps with noisy rooms and far-off voices.
    /// </summary>
    public const int SampleRate = 48000;

    public const int BitRate = 96000;

    private static readonly object StartupGate = new();
    private static bool _started;

    /// <returns>The .m4a written at <paramref name="pathWithoutExtension"/>.</returns>
    public static string Encode(string sourcePath, string pathWithoutExtension)
    {
        EnsureStarted();
        var path = pathWithoutExtension + ".m4a";
        using var reader = AudioConvert.Open(sourcePath);
        ISampleProvider samples = AudioConvert.ToMono(reader.ToSampleProvider());
        if (samples.WaveFormat.SampleRate != SampleRate) samples = new WdlResamplingSampleProvider(samples, SampleRate);

        // Media Foundation picks the container from the extension, so the partial file keeps .m4a.
        var tmp = pathWithoutExtension + ".part.m4a";
        MediaFoundationEncoder.EncodeToAac(samples.ToWaveProvider16(), tmp, BitRate);
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    private static void EnsureStarted()
    {
        lock (StartupGate)
        {
            if (_started) return;
            MediaFoundationApi.Startup();
            _started = true;
        }
    }
}
