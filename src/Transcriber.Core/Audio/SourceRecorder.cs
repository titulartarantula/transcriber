using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Transcriber.Core.Audio;

/// <summary>
/// Records one WASAPI endpoint to a WAV file in its native format, keeping the file aligned to a
/// session clock shared by every source.
/// </summary>
/// <remarks>
/// Loopback capture delivers no packets while nothing is playing, and a device can stall briefly.
/// Without correction those gaps collapse and the sources drift apart, so each packet is preceded
/// by enough silence to put it where the session clock says it belongs.
/// </remarks>
public sealed class SourceRecorder : IDisposable
{
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly Stopwatch _clock;
    private readonly object _gate = new();
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _blockAlign;
    private readonly int _sampleRate;
    private readonly bool _isFloat;
    private readonly bool _isPcm16;
    private WaveFileWriter? _writer;
    private long _framesWritten;
    private volatile float _peak;

    public SourceRecorder(string deviceId, SourceKind kind, string filePath, Stopwatch sessionClock)
    {
        _device = AudioDevices.Open(deviceId);
        _capture = kind == SourceKind.SystemAudio ? new WasapiLoopbackCapture(_device) : new WasapiCapture(_device);
        _clock = sessionClock;
        FilePath = filePath;

        var format = _capture.WaveFormat;
        _blockAlign = format.BlockAlign;
        _sampleRate = format.SampleRate;
        _isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == NAudio.Dmo.AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT);
        _isPcm16 = !_isFloat && format.BitsPerSample == 16;
        _writer = new WaveFileWriter(filePath, format);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    public string FilePath { get; }

    /// <summary>Set when the device failed mid-recording (unplugged, exclusive-mode grab, ...).</summary>
    public Exception? Error { get; private set; }

    /// <summary>Peak level of the most recent packet, 0..1, decaying when no packets arrive.</summary>
    public float ReadPeak()
    {
        var p = _peak;
        _peak = p * 0.6f;
        return p;
    }

    public void Start() => _capture.StartRecording();

    /// <summary>Stops capture and pads the file out to <paramref name="sessionLength"/>.</summary>
    public async Task StopAsync(TimeSpan sessionLength)
    {
        _capture.StopRecording();
        try
        {
            await _stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // The driver never confirmed; finalize with what was written.
        }

        lock (_gate)
        {
            if (_writer is null) return;
            PadTo((long)(sessionLength.TotalSeconds * _sampleRate), toleranceFrames: 0);
            _writer.Dispose();
            _writer = null;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0) return;
        int frames = e.BytesRecorded / _blockAlign;

        lock (_gate)
        {
            if (_writer is null) return;
            long expectedEnd = (long)(_clock.Elapsed.TotalSeconds * _sampleRate);
            PadTo(expectedEnd - frames, toleranceFrames: _sampleRate / 10);
            _writer.Write(e.Buffer, 0, frames * _blockAlign);
            _framesWritten += frames;
        }

        _peak = Math.Max(_peak, MeasurePeak(e.Buffer, frames * _blockAlign));
    }

    private void PadTo(long targetFrames, int toleranceFrames)
    {
        long missing = targetFrames - _framesWritten;
        if (missing <= toleranceFrames || missing <= 0) return;

        // Zero bytes are silence for both float and signed PCM, the only formats WASAPI shared mode yields.
        var chunk = new byte[(int)Math.Min(missing, _sampleRate) * _blockAlign];
        long remaining = missing;
        while (remaining > 0)
        {
            int n = (int)Math.Min(remaining, chunk.Length / _blockAlign);
            _writer!.Write(chunk, 0, n * _blockAlign);
            remaining -= n;
        }
        _framesWritten += missing;
    }

    private float MeasurePeak(byte[] buffer, int bytes)
    {
        float peak = 0;
        if (_isFloat)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytes));
            foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s));
        }
        else if (_isPcm16)
        {
            var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytes));
            foreach (var s in samples) peak = Math.Max(peak, Math.Abs(s / 32768f));
        }
        return Math.Min(peak, 1f);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null) Error = e.Exception;
        _stopped.TrySetResult();
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnDataAvailable;
        _capture.RecordingStopped -= OnRecordingStopped;
        _capture.Dispose();
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
        _device.Dispose();
    }
}
