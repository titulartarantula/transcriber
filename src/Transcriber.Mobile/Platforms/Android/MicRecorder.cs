using Android.Content;
using Android.Media;
using NAudio.Wave;
using Encoding = Android.Media.Encoding;

namespace Transcriber.Mobile;

/// <summary>
/// Records one input to a 16 kHz mono 16-bit WAV, the format Whisper and the diarizer use, so nothing
/// needs converting afterwards. Bluetooth headsets are routed over their voice (SCO/LE) link first.
/// </summary>
public sealed class MicRecorder
{
    public const int SampleRate = 16000;

    private readonly AudioManager _audio = (AudioManager)Platform.AppContext.GetSystemService(Context.AudioService)!;
    private readonly string _path;
    private AudioRecord? _record;
    private WaveFileWriter? _writer;
    private Thread? _thread;
    private volatile bool _running;
    private volatile float _peak;
    private long _frames;
    private bool _routedCommunication;
    private bool _routedSco;
    private Mode _previousMode;

    private MicRecorder(string path) => _path = path;

    public string FilePath => _path;

    public TimeSpan Elapsed => TimeSpan.FromSeconds(Interlocked.Read(ref _frames) / (double)SampleRate);

    /// <summary>Set if the input failed mid-recording (headset disconnected, mic taken by a call, ...).</summary>
    public Exception? Error { get; private set; }

    public static async Task<MicRecorder> StartAsync(MicOption mic, string path)
    {
        var recorder = new MicRecorder(path);
        try
        {
            if (mic.Kind == MicKind.Bluetooth) await recorder.RouteBluetoothAsync(mic);
            recorder.Begin(mic);
            return recorder;
        }
        catch
        {
            recorder.Unroute();
            throw;
        }
    }

    public float ReadPeak()
    {
        var p = _peak;
        _peak = p * 0.6f;
        return p;
    }

    public async Task StopAsync()
    {
        _running = false;
        try { _record?.Stop(); } catch (Java.Lang.IllegalStateException) { }
        if (_thread is not null) await Task.Run(() => _thread.Join(TimeSpan.FromSeconds(3)));
        _writer?.Dispose();
        _writer = null;
        _record?.Release();
        _record = null;
        Unroute();
    }

    private void Begin(MicOption mic)
    {
        int min = AudioRecord.GetMinBufferSize(SampleRate, ChannelIn.Mono, Encoding.Pcm16bit);
        int size = Math.Max(min, SampleRate * 2 / 2); // at least 500 ms so a busy UI never drops audio

        // VoiceRecognition skips most call-style processing on the built-in mic; Bluetooth voice links
        // only carry audio for the communication source.
        var source = mic.Kind == MicKind.Bluetooth ? AudioSource.VoiceCommunication : AudioSource.VoiceRecognition;
        _record = new AudioRecord(source, SampleRate, ChannelIn.Mono, Encoding.Pcm16bit, size);
        if (_record.State != State.Initialized)
            throw new InvalidOperationException("The microphone couldn't be opened. Another app may be using it.");
        if (MicDevices.Find(mic.DeviceId) is { } device) _record.SetPreferredDevice(device);

        _writer = new WaveFileWriter(_path, new WaveFormat(SampleRate, 16, 1));
        _record.StartRecording();
        if (_record.RecordingState != RecordState.Recording)
            throw new InvalidOperationException("The microphone didn't start. Another app may be using it.");

        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Transcriber mic" };
        _thread.Start();
    }

    private void Loop()
    {
        var samples = new short[SampleRate / 10];
        var bytes = new byte[samples.Length * 2];
        long lastFlush = 0;
        try
        {
            while (_running)
            {
                int n = _record!.Read(samples, 0, samples.Length);
                if (n < 0) throw new IOException($"The microphone stopped delivering audio (error {n}).");
                if (n == 0) continue;

                Buffer.BlockCopy(samples, 0, bytes, 0, n * 2);
                _writer!.Write(bytes, 0, n * 2);

                int peak = 0;
                for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs((int)samples[i]));
                _peak = Math.Max(_peak, peak / 32768f);

                long total = Interlocked.Add(ref _frames, n);
                // Keep the WAV header current so a crash still leaves a playable file.
                if (total - lastFlush >= SampleRate * 10)
                {
                    _writer.Flush();
                    lastFlush = total;
                }
            }
        }
        catch (Exception e)
        {
            if (_running) Error = e;
        }
    }

    private async Task RouteBluetoothAsync(MicOption mic)
    {
        _previousMode = _audio.Mode;
        _audio.Mode = Mode.InCommunication;

        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var target = _audio.AvailableCommunicationDevices
                .Where(d => d.Type is AudioDeviceType.BluetoothSco or AudioDeviceType.BleHeadset)
                .OrderByDescending(d => d.ProductNameFormatted?.ToString() == mic.Name)
                .FirstOrDefault();
            if (target is null || !_audio.SetCommunicationDevice(target))
                throw new InvalidOperationException($"Couldn't switch to {mic.Name}. Check that it's connected for calls, not just media.");
            _routedCommunication = true;
            return;
        }

        // Android 10–11: bring up the SCO link and wait for it to connect.
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new ScoReceiver(connected);
        Platform.AppContext.RegisterReceiver(receiver, new IntentFilter(AudioManager.ActionScoAudioStateUpdated));
        try
        {
            _audio.StartBluetoothSco();
            _audio.BluetoothScoOn = true;
            _routedSco = true;
            if (await Task.WhenAny(connected.Task, Task.Delay(5000)) != connected.Task)
                throw new InvalidOperationException($"{mic.Name} didn't open its microphone link. Try reconnecting the headset.");
        }
        finally
        {
            Platform.AppContext.UnregisterReceiver(receiver);
        }
    }

    private void Unroute()
    {
        try
        {
            if (_routedCommunication && OperatingSystem.IsAndroidVersionAtLeast(31)) _audio.ClearCommunicationDevice();
            if (_routedSco)
            {
                _audio.BluetoothScoOn = false;
                _audio.StopBluetoothSco();
            }
            if (_routedCommunication || _routedSco) _audio.Mode = _previousMode;
        }
        catch (Java.Lang.Exception)
        {
            // Routing cleanup is best effort; Android resets it when the app's audio session ends.
        }
        _routedCommunication = _routedSco = false;
    }

    private sealed class ScoReceiver(TaskCompletionSource connected) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            var state = intent?.GetIntExtra(AudioManager.ExtraScoAudioState, -1) ?? -1;
            if (state == (int)ScoAudioState.Connected) connected.TrySetResult();
        }
    }
}
