using Transcriber.Core;
using Transcriber.Core.Audio;
using Transcriber.Core.Jobs;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;

namespace Transcriber.Mobile;

public enum SessionMode { Idle, Recording }

public enum Tone { Busy, Success, Warning, Error }

public sealed record SessionStatus(Tone Tone, string Text, string? Detail = null);

/// <summary>
/// Owns the recording and the transcript queue for the life of the process, so both carry on while the
/// app is in the background and survive Android recreating the page or activity. Pages only display them.
/// A new recording can start while earlier ones are still being transcribed.
/// </summary>
public sealed class SessionController
{
    private const int NamesAlertId = 1002;
    private const int ResultAlertId = 1003;

    private readonly Dictionary<string, JobState> _alerted = [];
    private MicRecorder? _recorder;
    private MicOption? _mic;
    private DateTimeOffset _startedAt;
    private string _directory = "";
    private string _title = "";
    private string _label = "";
    private bool _split;
    private int _voices;
    private bool _started;
    private string? _notice;

    private SessionController()
    {
        Queue = new JobQueue(new JobStore(AppPaths.Recordings), () => new TranscriptionPipeline(LoadSettings()));
        Queue.Changed += job => MainThread.BeginInvokeOnMainThread(() => OnJobChanged(job));
        Queue.Removed += id => MainThread.BeginInvokeOnMainThread(() =>
        {
            JobRemoved?.Invoke(id);
            UpdateService();
        });
    }

    public static SessionController Instance { get; } = new();

    public JobQueue Queue { get; }

    public SessionMode Mode { get; private set; }

    public SessionStatus? Status { get; private set; }

    public TimeSpan Elapsed => _recorder?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Raised on the main thread whenever <see cref="Mode"/> or <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    /// <summary>Raised on the main thread when a transcript is added or changes.</summary>
    public event Action<TranscriptionJob>? JobChanged;

    /// <summary>Raised on the main thread when a transcript leaves the list.</summary>
    public event Action<string>? JobRemoved;

    public float ReadPeak() => _recorder?.ReadPeak() ?? 0f;

    /// <summary>Loads saved transcripts and resumes any that were interrupted. Call from the foreground.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        Queue.Start();
        foreach (var job in Queue.Jobs) _alerted[job.Id] = job.State;
        UpdateService();
    }

    public async Task StartAsync(MicOption mic, string title, string label, bool split)
    {
        if (Mode != SessionMode.Idle) return;
        (_mic, _title, _label, _split) = (mic, title, label, split);
        _voices = split ? SettingsStore.Load().Speakers.ExpectedSpeakers : 0;
        _directory = Path.Combine(AppPaths.Recordings, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_directory);

        Set(SessionMode.Recording, new SessionStatus(Tone.Busy, $"Starting {mic.Name}…"));
        UpdateService();
        try
        {
            _recorder = await MicRecorder.StartAsync(mic, Path.Combine(_directory, "mic.aac"));
            // Bluetooth routing can take a few seconds; the clock starts when audio does.
            _startedAt = DateTimeOffset.Now - _recorder.Elapsed;
        }
        catch (Exception ex)
        {
            Set(SessionMode.Idle, new SessionStatus(Tone.Error, "Couldn't start recording.", ex.Message));
            UpdateService();
            return;
        }

        Set(SessionMode.Recording, new SessionStatus(Tone.Busy, $"Recording from {mic.Name}…",
            "You can lock the phone or switch apps. Stop here or from the notification."));
    }

    /// <summary>Safe to call from the notification's Stop button while the app is in the background.</summary>
    public async Task StopAndTranscribeAsync()
    {
        if (Mode != SessionMode.Recording || _recorder is null) return;
        var recorder = _recorder;
        _recorder = null;
        await recorder.StopAsync();

        var label = string.IsNullOrWhiteSpace(_label) ? (_split ? "Speaker" : "Me") : _label.Trim();
        var recording = new SessionRecording(
            _title,
            _startedAt,
            recorder.Elapsed,
            _directory,
            [new RecordedSource(recorder.FilePath, label, SourceKind.Microphone, _split, _mic?.Name ?? "Microphone")]);
        Queue.Enqueue(recording, _voices);

        Set(SessionMode.Idle, recorder.Error is { } error
            ? new SessionStatus(Tone.Warning, "The microphone stopped early.", $"What was recorded is being transcribed. {error.Message}")
            : null);
        UpdateService();
    }

    private static AppSettings LoadSettings()
    {
        var settings = SettingsStore.Load();
        settings.Speakers.Diarize = true; // each recording's own switch decides for the single phone mic
        return settings;
    }

    private void OnJobChanged(TranscriptionJob job)
    {
        JobChanged?.Invoke(job);
        UpdateService();

        // Alert once per change of state, and only when the user can't already see it.
        if (_alerted.TryGetValue(job.Id, out var before) && before == job.State) return;
        _alerted[job.Id] = job.State;
        if (AppLifecycle.IsForeground) return;

        switch (job.State)
        {
            case JobState.NeedsNames:
                RecordingService.Alert(NamesAlertId, "Name the speakers",
                    $"“{job.Title}” is transcribed. Tap to say who was speaking and save the note.");
                break;
            case JobState.Saved:
                RecordingService.Alert(ResultAlertId, "Transcript saved", job.NoteDescription ?? job.Title);
                break;
            case JobState.Failed:
                RecordingService.Alert(ResultAlertId, "Transcription failed", $"“{job.Title}”: open Transcriber to retry. The recording is kept.");
                break;
        }
    }

    /// <summary>Keeps the foreground service up while recording or while transcripts are being made.</summary>
    private void UpdateService()
    {
        var jobs = Queue.Jobs;
        int active = jobs.Count(j => j.IsActive);
        string? text;
        if (Mode == SessionMode.Recording)
        {
            text = $"Recording from {_mic?.Name}" + (active > 0 ? $" · {Transcripts(active)} in progress" : "");
        }
        else if (active > 0)
        {
            var current = jobs.FirstOrDefault(j => j.State is JobState.Transcribing or JobState.Saving);
            text = current is null
                ? $"{Transcripts(active)} waiting"
                : $"Transcribing “{current.Title}”" + (active > 1 ? $" · {active - 1} more waiting" : "");
        }
        else
        {
            text = null;
        }

        var notice = text is null ? null : $"{Mode}:{text}";
        if (notice == _notice) return;
        _notice = notice;
        try
        {
            if (text is null) RecordingService.Hide();
            else RecordingService.Show(text, recording: Mode == SessionMode.Recording);
        }
        catch (Exception)
        {
            // Android won't start a foreground service from the background. The work carries on while
            // the process lives, and resumes from disk if it doesn't.
            _notice = null;
        }
    }

    private static string Transcripts(int n) => n == 1 ? "1 transcript" : $"{n} transcripts";

    private void Set(SessionMode mode, SessionStatus? status)
    {
        Mode = mode;
        Status = status;
        if (MainThread.IsMainThread) Changed?.Invoke();
        else MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }
}
