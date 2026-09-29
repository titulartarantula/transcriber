using NAudio.Wave;
using Transcriber.Audio.Windows;
using Transcriber.Core.Audio;
using Xunit;

namespace Transcriber.Tests;

public class AacEncoderTests
{
    [Fact]
    public void Compresses_a_loopback_capture_to_small_mono_aac_of_the_same_length()
    {
        var dir = Path.Combine(Path.GetTempPath(), "transcriber-aac-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            // Ten seconds of 48 kHz stereo float, as WASAPI loopback writes it.
            var raw = Path.Combine(dir, "source1-SystemAudio.wav");
            using (var writer = new WaveFileWriter(raw, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
            {
                for (int i = 0; i < 48000 * 10; i++)
                {
                    var s = 0.3f * MathF.Sin(2 * MathF.PI * 440 * i / 48000f);
                    writer.WriteSample(s);
                    writer.WriteSample(s);
                }
            }

            var m4a = AacEncoder.Encode(raw, Path.Combine(dir, "source1-SystemAudio-compact"));

            Assert.EndsWith("-compact.m4a", m4a);
            var bytes = new FileInfo(m4a).Length;
            Assert.InRange(bytes, 60_000, 180_000); // ~96 kbps for 10 s, against 3.8 MB raw
            Assert.True(AudioConvert.IsCompact(m4a, TimeSpan.FromSeconds(10)));
            Assert.Empty(Directory.GetFiles(dir, "*.part.*"));

            AudioConvert.ExternalDecoder ??= path => new AudioFileReader(path);
            var samples = AudioConvert.LoadMono16k(m4a);
            Assert.InRange(samples.Length / 16000.0, 9.8, 10.3);
            Assert.False(AudioConvert.IsSilent(samples));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
