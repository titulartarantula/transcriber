using Android.Media;
using NAudio.Wave;

namespace Transcriber.Mobile;

/// <summary>
/// Decodes a compressed file (the phone's own .aac recordings, or m4a/mp3) to 16-bit PCM with Android's
/// decoders, as a forward-only stream. Only needed when voices are separated on the phone.
/// </summary>
public sealed class MediaCodecReader : WaveStream
{
    private const long TimeoutUs = 10_000;
    private const int TryAgainLater = -1;

    private readonly MediaExtractor _extractor = new();
    private readonly MediaCodec _codec;
    private readonly MediaCodec.BufferInfo _info = new();
    private readonly long _length;
    private byte[] _pending = [];
    private int _pendingOffset;
    private long _position;
    private bool _inputDone;
    private bool _outputDone;

    public MediaCodecReader(string path)
    {
        _extractor.SetDataSource(path);
        int track = Enumerable.Range(0, _extractor.TrackCount)
            .FirstOrDefault(i => _extractor.GetTrackFormat(i).GetString(MediaFormat.KeyMime)?.StartsWith("audio/") == true, -1);
        if (track < 0) throw new InvalidDataException($"{Path.GetFileName(path)} has no audio.");
        _extractor.SelectTrack(track);

        var format = _extractor.GetTrackFormat(track);
        int rate = format.GetInteger(MediaFormat.KeySampleRate);
        int channels = format.GetInteger(MediaFormat.KeyChannelCount);
        WaveFormat = new WaveFormat(rate, 16, channels);
        // ADTS has no duration field; the estimate only sizes buffers.
        _length = format.ContainsKey(MediaFormat.KeyDuration)
            ? format.GetLong(MediaFormat.KeyDuration) * rate / 1_000_000 * WaveFormat.BlockAlign
            : new FileInfo(path).Length * 8 / AdtsAacWriter.BitRate * rate * WaveFormat.BlockAlign;

        _codec = MediaCodec.CreateDecoderByType(format.GetString(MediaFormat.KeyMime)!);
        _codec.Configure(format, null, null, 0);
        _codec.Start();
    }

    public override WaveFormat WaveFormat { get; }

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException("Decoded audio can only be read from start to end.");
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_pendingOffset < _pending.Length)
            {
                int n = Math.Min(count - written, _pending.Length - _pendingOffset);
                Buffer.BlockCopy(_pending, _pendingOffset, buffer, offset + written, n);
                _pendingOffset += n;
                written += n;
                continue;
            }
            if (_outputDone) break;
            Feed();
            Decode();
        }
        _position += written;
        return written;
    }

    private void Feed()
    {
        if (_inputDone) return;
        int index = _codec.DequeueInputBuffer(TimeoutUs);
        if (index < 0) return;
        var input = _codec.GetInputBuffer(index)!;
        int size = _extractor.ReadSampleData(input, 0);
        if (size < 0)
        {
            _codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
            _inputDone = true;
            return;
        }
        _codec.QueueInputBuffer(index, 0, size, _extractor.SampleTime, 0);
        _extractor.Advance();
    }

    private void Decode()
    {
        int index = _codec.DequeueOutputBuffer(_info, TimeoutUs);
        if (index == TryAgainLater || index < 0) return;

        if (_info.Size > 0)
        {
            var output = _codec.GetOutputBuffer(index)!;
            output.Position(_info.Offset);
            output.Limit(_info.Offset + _info.Size);
            _pending = new byte[_info.Size];
            output.Get(_pending, 0, _info.Size);
            _pendingOffset = 0;
        }
        _codec.ReleaseOutputBuffer(index, false);
        if ((_info.Flags & MediaCodecBufferFlags.EndOfStream) != 0) _outputDone = true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _codec.Stop(); } catch (Java.Lang.IllegalStateException) { }
            _codec.Release();
            _extractor.Release();
        }
        base.Dispose(disposing);
    }
}
