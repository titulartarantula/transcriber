using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Transcriber.Core.Audio;
using Transcriber.Core.Output;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;
using Transcriber.Core.Transcript;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Transcriber.App;

public partial class MainWindow : FluentWindow
{
    private enum Mode { Idle, Recording, Processing }

    private enum Tone { Busy, Success, Warning, Error }

    private readonly ObservableCollection<SourceItem> _sources = new();
    private readonly DispatcherTimer _timer;
    private AppSettings _settings;
    private RecordingSession? _session;
    private CancellationTokenSource? _processing;
    private Mode _mode = Mode.Idle;

    /// <summary>A recording whose transcription failed, kept so it can be retried.</summary>
    private SessionRecording? _failed;
    private string? _openTarget;
    private string? _folderTarget;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        SourcesList.ItemsSource = _sources;

        VoicesBox.ItemsSource = new[] { "Auto", "2", "3", "4", "5", "6", "8" };
        VoicesBox.SelectedItem = _settings.Speakers.ExpectedSpeakers > 0
            ? _settings.Speakers.ExpectedSpeakers.ToString()
            : "Auto";

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, OnTick, Dispatcher);
        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            LoadDevices();
            _timer.Start();
            if (string.IsNullOrWhiteSpace(_settings.Stt.BaseUrl) || _settings.Stt.BaseUrl == new SttSettings().BaseUrl)
                ShowStatus(Tone.Warning, "Set your speech-to-text server in Settings before recording.");
        };
    }

    // ----- Sources -------------------------------------------------------------------------

    private void LoadDevices()
    {
        SavePreferences();
        foreach (var item in _sources) item.Dispose();
        _sources.Clear();

        IReadOnlyList<AudioDeviceInfo> devices;
        try
        {
            devices = AudioDevices.List();
        }
        catch (Exception e)
        {
            ShowStatus(Tone.Error, "Could not list audio devices.", e.Message);
            return;
        }

        var prefs = _settings.Sources.ToDictionary(p => p.DeviceId);
        bool firstRun = prefs.Count == 0;
        int mics = 0, outputs = 0;
        foreach (var d in devices)
        {
            var defaultLabel = d.Kind == SourceKind.Microphone
                ? (mics++ == 0 ? "Me" : $"Mic {mics}")
                : (outputs++ == 0 ? "Remote" : $"Output {outputs}");
            prefs.TryGetValue(d.Id, out var pref);
            _sources.Add(new SourceItem(
                d,
                selected: pref?.Selected ?? (firstRun && d.IsDefault),
                label: string.IsNullOrWhiteSpace(pref?.Label) ? "" : pref.Label,
                diarize: pref?.Diarize ?? d.Kind == SourceKind.SystemAudio)
            {
                DefaultLabel = defaultLabel,
            });
        }

        if (_sources.Count == 0)
            ShowStatus(Tone.Warning, "No active audio devices were found.");
        else if (_sources.All(s => s.Device.Kind != SourceKind.Microphone))
            ShowStatus(Tone.Warning, "No microphones found.", AudioDevices.IsRemoteDesktopSession
                ? "You're in a Remote Desktop session, which only sees the audio your RDP client redirects. In Remote Desktop " +
                  "Connection: Show Options → Local Resources → Remote audio → Settings → Remote audio recording → " +
                  "\"Record from this computer\", then reconnect and press Refresh."
                : "Check that a microphone is plugged in and enabled in Windows Sound settings, then press Refresh.");
    }

    /// <summary>Remembers source choices, keeping entries for devices that are currently unplugged.</summary>
    private void SavePreferences()
    {
        if (_sources.Count == 0) return;
        var byId = _settings.Sources.ToDictionary(p => p.DeviceId);
        foreach (var item in _sources)
        {
            byId[item.Device.Id] = new SourcePreference
            {
                DeviceId = item.Device.Id,
                Selected = item.Selected,
                Label = item.Label.Trim(),
                Diarize = item.Diarize,
            };
        }
        _settings.Sources = byId.Values.ToList();
        TrySaveSettings();
    }

    private void TrySaveSettings()
    {
        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus(Tone.Warning, "Settings could not be saved.", e.Message);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadDevices();

    private void OnVoicesChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _settings.Speakers.ExpectedSpeakers = int.TryParse(VoicesBox.SelectedItem as string, out var n) ? n : 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_mode == Mode.Recording && _session is not null)
        {
            ElapsedText.Text = MarkdownRenderer.Clock(_session.Elapsed);
            var peaks = _session.ReadPeaks();
            var recording = _sources.Where(s => s.Selected).ToList();
            for (int i = 0; i < recording.Count && i < peaks.Length; i++) recording[i].Level = Scale(peaks[i]);
        }
        else if (_mode == Mode.Idle)
        {
            foreach (var s in _sources) s.Level = Scale(s.ReadSystemMeter());
        }
    }

    /// <summary>Square root makes quiet speech visible on a linear bar.</summary>
    private static double Scale(float peak) => Math.Sqrt(Math.Clamp(peak, 0f, 1f));

    // ----- Recording -----------------------------------------------------------------------

    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Recording) await StopAndTranscribeAsync();
        else if (_mode == Mode.Idle) StartRecording();
    }

    private void StartRecording()
    {
        var chosen = _sources.Where(s => s.Selected).ToList();
        if (chosen.Count == 0)
        {
            ShowStatus(Tone.Warning, "Tick at least one audio source to record.");
            return;
        }

        SavePreferences();
        var selections = chosen
            .Select(s => new SourceSelection(s.Device, s.EffectiveLabel, s.Diarize))
            .ToList();
        try
        {
            _session = RecordingSession.Start(TitleBox.Text.Trim(), selections);
        }
        catch (Exception ex)
        {
            ShowStatus(Tone.Error, "Could not start recording.", ex.Message);
            return;
        }

        _failed = null;
        SetMode(Mode.Recording);
        ShowStatus(Tone.Busy, $"Recording {string.Join(", ", selections.Select(s => s.Label))}…",
            "Press Stop when you're done. The transcript is made after the recording ends.");
    }

    private async Task StopAndTranscribeAsync()
    {
        var session = _session!;
        _session = null;
        var recording = await session.StopAsync();
        var dropped = session.Failures.Select(f => $"{f.Device} stopped early: {f.Error.Message}").ToList();
        await TranscribeAsync(recording, dropped);
    }

    private async void OnTranscribeFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose an audio file to transcribe",
            Filter = "Audio and video|*.wav;*.mp3;*.m4a;*.mp4;*.aac;*.wma;*.flac|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;

        SessionRecording recording;
        try
        {
            recording = TranscriptionPipeline.FromFile(dialog.FileName, "Speaker", diarize: true);
        }
        catch (Exception ex)
        {
            ShowStatus(Tone.Error, "That file could not be opened as audio.", ex.Message);
            return;
        }

        if (!string.IsNullOrWhiteSpace(TitleBox.Text)) recording = recording with { Title = TitleBox.Text.Trim() };
        await TranscribeAsync(recording, []);
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        if (_failed is { } recording) await TranscribeAsync(recording, []);
    }

    private async Task TranscribeAsync(SessionRecording recording, IReadOnlyList<string> earlierWarnings)
    {
        SetMode(Mode.Processing);
        _processing = new CancellationTokenSource();
        var progress = new Progress<string>(m => ShowStatus(Tone.Busy, m));
        var pipeline = new TranscriptionPipeline(_settings.Clone(), ReviewSpeakersAsync);
        ShowStatus(Tone.Busy, "Starting transcription…");

        try
        {
            var result = await Task.Run(() => pipeline.RunAsync(recording, progress, _processing.Token));
            _failed = null;
            var warnings = earlierWarnings.Concat(result.Warnings).ToList();
            var headline = result.UtteranceCount == 0
                ? "No speech was found, but an empty note was saved."
                : $"Saved {result.Note.Description}";
            ShowStatus(warnings.Count > 0 ? Tone.Warning : Tone.Success, headline, string.Join("\n", warnings),
                open: result.Note.LocalPath, folder: result.Note.LocalPath is { } p ? Path.GetDirectoryName(p) : null);
            TitleBox.Text = "";
        }
        catch (OperationCanceledException)
        {
            _failed = recording;
            ShowStatus(Tone.Warning, "Transcription cancelled.", $"The recording is kept in {recording.Directory}.",
                folder: recording.Directory, retry: true);
        }
        catch (Exception ex)
        {
            _failed = recording;
            ShowStatus(Tone.Error, "Transcription failed.", $"{ex.Message}\nThe recording is kept in {recording.Directory}.",
                folder: recording.Directory, retry: true);
        }
        finally
        {
            _processing.Dispose();
            _processing = null;
            SetMode(Mode.Idle);
        }
    }

    /// <summary>Called from the pipeline's worker thread; hops to the UI for the naming dialog.</summary>
    private Task<IReadOnlyDictionary<string, string>?> ReviewSpeakersAsync(IReadOnlyList<SpeakerSummary> speakers, CancellationToken ct) =>
        Dispatcher.InvokeAsync(() =>
        {
            var dialog = new SpeakerReviewWindow(speakers) { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Names : null;
        }).Task;

    private void OnCancel(object sender, RoutedEventArgs e) => _processing?.Cancel();

    // ----- Status and state ----------------------------------------------------------------

    private void SetMode(Mode mode)
    {
        _mode = mode;
        bool idle = mode == Mode.Idle;
        SourcesList.IsEnabled = idle;
        RefreshButton.IsEnabled = idle;
        FileButton.IsEnabled = idle;
        TitleBox.IsEnabled = mode != Mode.Processing;
        VoicesBox.IsEnabled = idle;
        CancelButton.Visibility = mode == Mode.Processing ? Visibility.Visible : Visibility.Collapsed;

        RecordButton.IsEnabled = mode != Mode.Processing;
        RecordButton.Content = mode == Mode.Recording ? "Stop and transcribe" : "Start recording";
        RecordButton.Icon = new SymbolIcon(mode == Mode.Recording ? SymbolRegular.Stop24 : SymbolRegular.Record24);
        RecordButton.Appearance = mode == Mode.Recording ? ControlAppearance.Danger : ControlAppearance.Primary;

        if (mode == Mode.Idle) ElapsedText.Text = "00:00:00";
        if (mode != Mode.Recording) foreach (var s in _sources) s.Level = 0;
    }

    private void ShowStatus(Tone tone, string text, string? detail = null, string? open = null, string? folder = null, bool retry = false)
    {
        StatusCard.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusDetail.Text = detail ?? "";
        StatusDetail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;

        BusyRing.Visibility = tone == Tone.Busy ? Visibility.Visible : Visibility.Collapsed;
        StatusIcon.Visibility = tone == Tone.Busy ? Visibility.Collapsed : Visibility.Visible;
        (StatusIcon.Symbol, var brushKey) = tone switch
        {
            Tone.Success => (SymbolRegular.CheckmarkCircle24, "SystemFillColorSuccessBrush"),
            Tone.Warning => (SymbolRegular.Warning24, "SystemFillColorCautionBrush"),
            Tone.Error => (SymbolRegular.ErrorCircle24, "SystemFillColorCriticalBrush"),
            _ => (SymbolRegular.Info24, "TextFillColorSecondaryBrush"),
        };
        StatusIcon.Foreground = TryFindResource(brushKey) as Brush ?? Brushes.Gray;

        _openTarget = open;
        _folderTarget = folder;
        OpenButton.Visibility = open is null ? Visibility.Collapsed : Visibility.Visible;
        FolderButton.Visibility = folder is null ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (_openTarget is not null) Shell(_openTarget);
    }

    private void OnShowFolder(object sender, RoutedEventArgs e)
    {
        if (_openTarget is not null && File.Exists(_openTarget))
            Process.Start("explorer.exe", $"/select,\"{_openTarget}\"");
        else if (_folderTarget is not null)
            Shell(_folderTarget);
    }

    private void Shell(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowStatus(Tone.Error, $"Could not open {target}.", ex.Message);
        }
    }

    // ----- Settings and lifetime -----------------------------------------------------------

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings.Clone()) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var sources = _settings.Sources;
        _settings = dialog.Result;
        _settings.Sources = sources;
        TrySaveSettings();
        if (StatusText.Text.StartsWith("Set your speech-to-text server")) StatusCard.Visibility = Visibility.Collapsed;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_mode != Mode.Idle)
        {
            var what = _mode == Mode.Recording ? "A recording is in progress" : "A transcription is running";
            var answer = System.Windows.MessageBox.Show(
                $"{what}. Quit anyway? Audio recorded so far stays in the recordings folder.",
                "Transcriber", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _processing?.Cancel();
            _session?.Dispose();
        }

        _timer.Stop();
        SavePreferences();
        foreach (var item in _sources) item.Dispose();
    }
}
