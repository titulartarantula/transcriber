using Transcriber.Core;
using Transcriber.Core.Audio;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;
using Transcriber.Core.Transcript;

namespace Transcriber.Mobile;

public enum SessionMode { Idle, Recording, Processing }

public enum Tone { Busy, Success, Warning, Error }

public sealed record SessionStatus(Tone Tone, string Text, string? Detail = null, bool CanRetry = false);

/// <summary>
/// Owns the recording and its transcription for the life of the process, so both carry on while the
/// app is in the background and survive Android recreating the page or activity. Pages only display it.
/// </summary>
public sealed class SessionController
{
    private const int ReviewAlertId = 1002;
    private const int ResultAlertId = 1003;

    private MicRecorder? _recorder;
    private MicOption? _mic;
    private DateTimeOffset _startedAt;
    private string _directory = "";
    private string _title = "";
    private string _label = "";
    private bool _split;
    private CancellationTokenSource? _processing;
    private SessionRecording? _failed;

    private SessionController()
    {
    }

    public static SessionController Instance { get; } = new();

    public SessionMode Mode { get; private set; }

    public SessionStatus? Status { get; private set; }

    public TimeSpan Elapsed => _recorder?.Elapsed ?? TimeSpan.Zero;

    /// <summary>Raised on the main thread whenever <see cref="Mode"/> or <see cref="Status"/> changes.</summary>
    public event Action? Changed;

    public float ReadPeak() => _recorder?.ReadPeak() ?? 0f;

    public async Task StartAsync(MicOption mic, string title, string label, bool split)
    {
        if (Mode != SessionMode.Idle) return;
        (_mic, _title, _label, _split) = (mic, title, label, split);
        _directory = Path.Combine(AppPaths.Recordings, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(_directory);

        RecordingService.Show($"Recording from {mic.Name}", recording: true);
        Set(SessionMode.Recording, new SessionStatus(Tone.Busy, $"Starting {mic.Name}…"));
        try
        {
            _recorder = await MicRecorder.StartAsync(mic, Path.Combine(_directory, "mic.wav"));
            // Bluetooth routing can take a few seconds; the clock starts when audio does.
            _startedAt = DateTimeOffset.Now - _recorder.Elapsed;
        }
        catch (Exception ex)
        {
            RecordingService.Hide();
            Set(SessionMode.Idle, new SessionStatus(Tone.Error, "Couldn't start recording.", ex.Message));
            return;
        }

        _failed = null;
        Set(SessionMode.Recording, new SessionStatus(Tone.Busy, $"Recording from {mic.Name}…",
            "You can lock the phone or switch apps. Stop here or from the notification."));
    }

    /// <summary>Safe to call from the notification's Stop button while the app is in the background.</summary>
    public async Task StopAndTranscribeAsync()
    {
        if (Mode != SessionMode.Recording || _recorder is null) return;
        var recorder = _recorder;
        _recorder = null;
        Set(SessionMode.Processing, new SessionStatus(Tone.Busy, "Finishing the recording…"));
        await recorder.StopAsync();

        var label = string.IsNullOrWhiteSpace(_label) ? (_split ? "Speaker" : "Me") : _label.Trim();
        var recording = new SessionRecording(
            _title,
            _startedAt,
            recorder.Elapsed,
            _directory,
            [new RecordedSource(recorder.FilePath, label, SourceKind.Microphone, _split, _mic?.Name ?? "Microphone")]);

        var warnings = recorder.Error is { } error ? new List<string> { $"The microphone stopped early: {error.Message}" } : [];
        await TranscribeAsync(recording, warnings);
    }

    public Task RetryAsync() =>
        Mode == SessionMode.Idle && _failed is { } recording ? TranscribeAsync(recording, []) : Task.CompletedTask;

    public void Cancel() => _processing?.Cancel();

    private async Task TranscribeAsync(SessionRecording recording, IReadOnlyList<string> earlierWarnings)
    {
        RecordingService.Show("Transcribing…", recording: false);
        RecordingService.CancelAlert(ResultAlertId);
        Set(SessionMode.Processing, new SessionStatus(Tone.Busy, "Starting transcription…"));
        _processing = new CancellationTokenSource();

        var settings = SettingsStore.Load();
        settings.Speakers.Diarize = true; // the per-recording switch decides for the single phone mic
        var pipeline = new TranscriptionPipeline(settings, ReviewSpeakersAsync);
        var progress = new Progress<string>(m => Set(SessionMode.Processing, new SessionStatus(Tone.Busy, m)));

        try
        {
            var result = await Task.Run(() => pipeline.RunAsync(recording, progress, _processing.Token));
            _failed = null;
            var warnings = earlierWarnings.Concat(result.Warnings).ToList();
            var text = result.UtteranceCount == 0 ? "No speech was found, but an empty note was saved." : $"Saved to {result.Note.Description}";
            Set(SessionMode.Processing, new SessionStatus(warnings.Count > 0 ? Tone.Warning : Tone.Success, text, string.Join("\n", warnings)));
            if (!AppLifecycle.IsForeground) RecordingService.Alert(ResultAlertId, "Transcript saved", result.Note.Description);
        }
        catch (OperationCanceledException)
        {
            _failed = recording;
            Set(SessionMode.Processing, new SessionStatus(Tone.Warning, "Transcription cancelled. The recording is kept on the phone.", CanRetry: true));
        }
        catch (Exception ex)
        {
            _failed = recording;
            Set(SessionMode.Processing, new SessionStatus(Tone.Error, "Transcription failed. The recording is kept on the phone.", ex.Message, CanRetry: true));
            if (!AppLifecycle.IsForeground) RecordingService.Alert(ResultAlertId, "Transcription failed", "Open Transcriber to retry. The recording is kept.");
        }
        finally
        {
            _processing.Dispose();
            _processing = null;
            RecordingService.CancelAlert(ReviewAlertId);
            RecordingService.Hide();
            Set(SessionMode.Idle, Status);
        }
    }

    /// <summary>
    /// Shows the naming page. Android can't show a page while the app is in the background, so if it is,
    /// post a notification and wait until the user comes back.
    /// </summary>
    private Task<IReadOnlyDictionary<string, string>?> ReviewSpeakersAsync(IReadOnlyList<SpeakerSummary> speakers, CancellationToken ct) =>
        MainThread.InvokeOnMainThreadAsync(async () =>
        {
            if (!AppLifecycle.IsForeground)
            {
                RecordingService.Show("Waiting for you to name the speakers", recording: false);
                RecordingService.Alert(ReviewAlertId, "Name the speakers", "Tap to say who was speaking and save the note.");
                await AppLifecycle.WaitForForegroundAsync(ct);
                RecordingService.CancelAlert(ReviewAlertId);
                RecordingService.Show("Transcribing…", recording: false);
            }

            var navigation = Application.Current?.Windows.FirstOrDefault()?.Page?.Navigation
                ?? throw new InvalidOperationException("The app window isn't available to show the naming screen.");
            var page = new SpeakerReviewPage(speakers);
            await navigation.PushModalAsync(page);
            return await page.Result;
        });

    private void Set(SessionMode mode, SessionStatus? status)
    {
        Mode = mode;
        Status = status;
        if (MainThread.IsMainThread) Changed?.Invoke();
        else MainThread.BeginInvokeOnMainThread(() => Changed?.Invoke());
    }
}
