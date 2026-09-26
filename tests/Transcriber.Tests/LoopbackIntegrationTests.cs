using System.Diagnostics;
using Transcriber.Audio.Windows;
using Transcriber.Core.Audio;
using Transcriber.Core.Settings;
using Transcriber.Core.Stt;
using Transcriber.Core.Transcript;
using Xunit;
using Xunit.Abstractions;

namespace Transcriber.Tests;

/// <summary>
/// Records the default output through WASAPI loopback while Windows TTS speaks. Plays audible
/// speech, so it only runs when TRANSCRIBER_LOOPBACK_TEST=1.
/// </summary>
public class LoopbackIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Loopback_recording_keeps_silence_so_timestamps_stay_aligned()
    {
        if (Environment.GetEnvironmentVariable("TRANSCRIBER_LOOPBACK_TEST") != "1") return;

        var device = AudioDevices.List().First(d => d.Kind == SourceKind.SystemAudio && d.IsDefault);
        output.WriteLine($"Recording {device.Name}");
        var root = Path.Combine(Path.GetTempPath(), "transcriber-loopback-" + Guid.NewGuid());

        using var session = RecordingSession.Start("loopback", [new SourceSelection(device, "Remote", false)], root);
        // Nothing is playing yet, so loopback delivers no packets; this stretch must be padded with silence.
        await Task.Delay(2000);
        await Speak("The quarterly numbers look good. Let's review the budget next week.");
        await Task.Delay(1000);
        var recording = await session.StopAsync();

        var samples = AudioConvert.LoadMono16k(recording.Sources[0].FilePath);
        double seconds = samples.Length / (double)AudioConvert.SampleRate;
        output.WriteLine($"Session {recording.Duration.TotalSeconds:F2}s, file {seconds:F2}s");

        Assert.InRange(seconds, recording.Duration.TotalSeconds - 0.25, recording.Duration.TotalSeconds + 0.25);
        Assert.True(AudioConvert.IsSilent(samples[..(AudioConvert.SampleRate * 3 / 2)]), "lead-in should be silent");
        Assert.False(AudioConvert.IsSilent(samples), "speech should have been captured");

        var url = Environment.GetEnvironmentVariable("TRANSCRIBER_STT_URL");
        if (!string.IsNullOrEmpty(url))
        {
            var wav16 = Path.Combine(root, "check-16k.wav");
            AudioConvert.WriteWav16(wav16, samples);
            using var stt = new WhisperClient(new SttSettings { BaseUrl = url });
            var words = WordExtractor.Extract(await stt.TranscribeAsync(wav16));
            output.WriteLine(string.Join("", words.Select(w => w.Text)));
            output.WriteLine($"First word at {words[0].Start:F2}s");
            Assert.True(words[0].Start >= 1.8, "first word should land after the silent lead-in");
            Assert.Contains(words, w => w.Text.Contains("budget", StringComparison.OrdinalIgnoreCase));
        }

        Directory.Delete(root, true);
    }

    private static async Task Speak(string text)
    {
        var script = "Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; " +
                     $"$s.Speak('{text.Replace("'", "''")}')";
        using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await p.WaitForExitAsync();
    }
}
