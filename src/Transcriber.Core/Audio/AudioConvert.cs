using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Transcriber.Core.Audio;

public static class AudioConvert
{
    /// <summary>Whisper and the diarization models both expect 16 kHz mono.</summary>
    public const int SampleRate = 16000;

    /// <summary>Decodes any file Media Foundation understands to 16 kHz mono float samples.</summary>
    public static float[] LoadMono16k(string path)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels > 1) provider = new DownmixToMono(provider);
        if (provider.WaveFormat.SampleRate != SampleRate) provider = new WdlResamplingSampleProvider(provider, SampleRate);

        var estimate = (int)Math.Min(int.MaxValue, reader.TotalTime.TotalSeconds * SampleRate + SampleRate);
        var samples = new List<float>(estimate);
        var buffer = new float[SampleRate];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read));
        return samples.ToArray();
    }

    public static void WriteWav16(string path, float[] samples)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(SampleRate, 16, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }

    /// <summary>
    /// True when the loudest second of audio is still below roughly -60 dBFS, i.e. a muted mic or an
    /// output that played nothing. Sending that to Whisper only invites hallucinated text.
    /// </summary>
    public static bool IsSilent(float[] samples, double thresholdRms = 0.001)
    {
        for (int start = 0; start < samples.Length; start += SampleRate)
        {
            int end = Math.Min(samples.Length, start + SampleRate);
            double sum = 0;
            for (int i = start; i < end; i++) sum += samples[i] * samples[i];
            if (Math.Sqrt(sum / (end - start)) >= thresholdRms) return false;
        }
        return true;
    }

    private sealed class DownmixToMono : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = Array.Empty<float>();

        public DownmixToMono(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            int needed = count * _channels;
            if (_buffer.Length < needed) _buffer = new float[needed];
            int read = _source.Read(_buffer, 0, needed);
            int frames = read / _channels;
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++) sum += _buffer[f * _channels + c];
                buffer[offset + f] = sum / _channels;
            }
            return frames;
        }
    }
}
