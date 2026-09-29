using Android.Media;

namespace Transcriber.Mobile;

/// <summary>
/// Encodes 16-bit mono PCM to AAC-LC with Android's encoder and writes it as raw ADTS (.aac). Every ADTS
/// frame carries its own header, so if the app dies mid-recording the file still plays up to the last
/// frame written. An .m4a would be unreadable without the index written at the end.
/// </summary>
public sealed class AdtsAacWriter : IDisposable
{
    public const int BitRate = 48000;

    private const long TimeoutUs = 10_000;
    private const int TryAgainLater = -1;

    private readonly MediaCodec _codec;
    private readonly FileStream _file;
    private readonly MediaCodec.BufferInfo _info = new();
    private readonly byte[] _header;
    private readonly int _sampleRate;
    private byte[] _frame = new byte[2048];
    private long _framesIn;
    private bool _finished;

    public AdtsAacWriter(string path, int sampleRate)
    {
        _sampleRate = sampleRate;
        _header = AdtsHeaderTemplate(sampleRate);

        var format = MediaFormat.CreateAudioFormat(MediaFormat.MimetypeAudioAac, sampleRate, 1);
        format.SetInteger(MediaFormat.KeyAacProfile, (int)MediaCodecProfileType.Aacobjectlc);
        format.SetInteger(MediaFormat.KeyBitRate, BitRate);
        format.SetInteger(MediaFormat.KeyMaxInputSize, sampleRate * 2);
        _codec = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeAudioAac);
        _codec.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        _codec.Start();
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
    }

    /// <summary>Encodes whatever the encoder can take now; call from the recording thread.</summary>
    public void Write(byte[] pcm, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int index = _codec.DequeueInputBuffer(TimeoutUs);
            if (index < 0)
            {
                Drain(endOfStream: false);
                continue;
            }
            var input = _codec.GetInputBuffer(index)!;
            input.Clear();
            int n = Math.Min(count - offset, input.Remaining());
            input.Put(pcm, offset, n);
            _codec.QueueInputBuffer(index, 0, n, TimestampUs(), 0);
            _framesIn += n / 2;
            offset += n;
        }
        Drain(endOfStream: false);
    }

    /// <summary>Writes buffered audio to storage, so a crash loses at most what came since.</summary>
    public void Flush() => _file.Flush(flushToDisk: true);

    /// <summary>Encodes the tail and closes the file.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        int index = _codec.DequeueInputBuffer(TimeoutUs * 50);
        if (index >= 0)
        {
            _codec.QueueInputBuffer(index, 0, 0, TimestampUs(), MediaCodecBufferFlags.EndOfStream);
            Drain(endOfStream: true);
        }
        _file.Flush(flushToDisk: true);
    }

    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _codec.Stop();
            _codec.Release();
            _file.Dispose();
        }
    }

    private long TimestampUs() => _framesIn * 1_000_000 / _sampleRate;

    private void Drain(bool endOfStream)
    {
        // At the end, keep asking until the encoder says it has flushed everything (or gives up).
        int idle = 0;
        while (true)
        {
            int index = _codec.DequeueOutputBuffer(_info, endOfStream ? TimeoutUs : 0);
            if (index == TryAgainLater)
            {
                if (!endOfStream || ++idle > 100) return;
                continue;
            }
            if (index < 0) continue; // output format or buffers changed; nothing to write

            if ((_info.Flags & MediaCodecBufferFlags.CodecConfig) == 0 && _info.Size > 0)
            {
                var output = _codec.GetOutputBuffer(index)!;
                output.Position(_info.Offset);
                output.Limit(_info.Offset + _info.Size);
                if (_frame.Length < _info.Size) _frame = new byte[_info.Size];
                output.Get(_frame, 0, _info.Size);
                WriteHeader(_info.Size);
                _file.Write(_frame, 0, _info.Size);
            }
            _codec.ReleaseOutputBuffer(index, false);
            if ((_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) return;
        }
    }

    private void WriteHeader(int payload)
    {
        int length = payload + 7;
        _header[3] = (byte)((_header[3] & 0xC0) | (length >> 11));
        _header[4] = (byte)(length >> 3);
        _header[5] = (byte)(((length & 7) << 5) | 0x1F);
        _file.Write(_header, 0, 7);
    }

    /// <summary>A 7-byte ADTS header for AAC-LC mono, with the frame length filled in per frame.</summary>
    private static byte[] AdtsHeaderTemplate(int sampleRate)
    {
        int[] rates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];
        int rateIndex = Array.IndexOf(rates, sampleRate);
        if (rateIndex < 0) throw new ArgumentException($"AAC has no ADTS index for {sampleRate} Hz.", nameof(sampleRate));
        const int profile = 1; // AAC-LC, stored as object type - 1
        const int channels = 1;
        return
        [
            0xFF, 0xF1, // sync word, MPEG-4, no CRC
            (byte)((profile << 6) | (rateIndex << 2) | (channels >> 2)),
            (byte)((channels & 3) << 6),
            0, 0x1F, 0xFC,
        ];
    }
}
