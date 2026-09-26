using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Transcriber.Core.Audio;

public static class AudioConvert
{
    /// <summary>Whisper and the diarization models both expect 16 kHz mono.</summary>
    public const int SampleRate = 16000;

    /// <summary>
    /// Opens non-WAV files (mp3, m4a, ...). Set by the platform: Media Foundation on Windows. WAV is
    /// always read directly, so recordings work everywhere without it.
    /// </summary>
    public static Func<string, WaveStream>? ExternalDecoder { get; set; }

    private static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Opens an audio file as a stream NAudio can turn into float samples.</summary>
    public static WaveStream Open(string path)
    {
        if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            return ExternalDecoder?.Invoke(path)
                ?? throw new NotSupportedException($"{Path.GetExtension(path)} files can't be decoded on this device; use WAV.");
        }

        var wav = new WaveFileReader(path);
        var f = wav.WaveFormat;
        if (f.Encoding != WaveFormatEncoding.Extensible) return wav;

        // WASAPI writes WAVE_FORMAT_EXTENSIBLE, which NAudio's sample converters don't accept. Relabel it
        // as the plain float or PCM format its sub-format GUID says it is.
        var plain = ExtensibleSubFormat(f) == FloatSubFormat
            ? WaveFormat.CreateIeeeFloatWaveFormat(f.SampleRate, f.Channels)
            : new WaveFormat(f.SampleRate, f.BitsPerSample, f.Channels);
        return new RelabelledStream(wav, plain);
    }

    /// <summary>The SubFormat GUID from WAVEFORMATEXTENSIBLE's extra bytes (after valid-bits and channel mask).</summary>
    private static Guid? ExtensibleSubFormat(WaveFormat format)
    {
        byte[]? extra = format switch
        {
            WaveFormatExtraData d => d.ExtraData,
            _ => null,
        };
        return extra is { Length: >= 22 } ? new Guid(extra.AsSpan(6, 16)) : null;
    }

    public static TimeSpan GetDuration(string path)
    {
        using var stream = Open(path);
        return stream.TotalTime;
    }

    /// <summary>Decodes an audio file to 16 kHz mono float samples.</summary>
    public static float[] LoadMono16k(string path)
    {
        using var reader = Open(path);
        ISampleProvider provider = reader.ToSampleProvider();
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

    /// <summary>Reads another stream's bytes under a different format header, and owns (disposes) it.</summary>
    private sealed class RelabelledStream(WaveStream inner, WaveFormat format) : WaveStream
    {
        public override WaveFormat WaveFormat => format;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
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
