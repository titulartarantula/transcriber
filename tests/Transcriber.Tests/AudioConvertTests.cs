using NAudio.Wave;
using Transcriber.Core.Audio;
using Xunit;

namespace Transcriber.Tests;

public class AudioConvertTests
{
    /// <summary>WASAPI capture writes 48 kHz stereo float as WAVE_FORMAT_EXTENSIBLE; it must decode as float, not int.</summary>
    [Fact]
    public void Decodes_extensible_float_wav_to_16k_mono()
    {
        var path = Path.Combine(Path.GetTempPath(), $"transcriber-ext-{Guid.NewGuid()}.wav");
        try
        {
            var format = new WaveFormatExtensible(48000, 32, 2);
            using (var writer = new WaveFileWriter(path, format))
            {
                // Raw float bytes, as WASAPI delivers them (WriteSamples would convert to int for EXTENSIBLE).
                var frame = new float[2];
                for (int i = 0; i < 48000 * 2; i++)
                {
                    frame[0] = frame[1] = 0.5f * MathF.Sin(2 * MathF.PI * 440 * i / 48000f);
                    var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.AsSpan()).ToArray();
                    writer.Write(bytes, 0, bytes.Length);
                }
            }

            var samples = AudioConvert.LoadMono16k(path);

            Assert.InRange(samples.Length, 16000 * 2 - 200, 16000 * 2 + 200);
            double rms = Math.Sqrt(samples.Average(s => (double)s * s));
            Assert.InRange(rms, 0.33, 0.38); // 0.5 amplitude sine → RMS ≈ 0.354
            Assert.Equal(2.0, AudioConvert.GetDuration(path).TotalSeconds, precision: 2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Decodes_pcm16_mono_16k_unchanged_in_length()
    {
        var path = Path.Combine(Path.GetTempPath(), $"transcriber-pcm-{Guid.NewGuid()}.wav");
        try
        {
            var tone = Enumerable.Range(0, 16000).Select(i => 0.25f * MathF.Sin(2 * MathF.PI * 300 * i / 16000f)).ToArray();
            AudioConvert.WriteWav16(path, tone);

            var samples = AudioConvert.LoadMono16k(path);

            Assert.Equal(16000, samples.Length);
            Assert.False(AudioConvert.IsSilent(samples));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Non_wav_without_a_decoder_is_a_clear_error()
    {
        var old = AudioConvert.ExternalDecoder;
        AudioConvert.ExternalDecoder = null;
        try
        {
            var e = Assert.Throws<NotSupportedException>(() => AudioConvert.Open("meeting.m4a"));
            Assert.Contains(".m4a", e.Message);
        }
        finally
        {
            AudioConvert.ExternalDecoder = old;
        }
    }
}
