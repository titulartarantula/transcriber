using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Transcriber.Audio.Windows;
using Transcriber.Core;
using Transcriber.Core.Audio;
using Transcriber.Core.Jobs;
using Transcriber.Core.Output;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Transcriber.App;

public partial class MainWindow : FluentWindow
{
    private enum Mode { Idle, Recording }

    private enum Tone { Busy, Success, Warning, Error }

    private readonly ObservableCollection<SourceItem> _sources = new();
    private readonly ObservableCollection<JobItem> _jobs = new();
    private readonly DispatcherTimer _timer;
    private readonly JobQueue _queue;
    private AppSettings _settings;
    private RecordingSession? _session;
    private Mode _mode = Mode.Idle;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsStore.Load();
        SourcesList.ItemsSource = _sources;
        JobsList.ItemsSource = _jobs;

        VoicesBox.ItemsSource = new[] { "Auto", "2", "3", "4", "5", "6", "7", "8" };
        VoicesBox.SelectedItem = _settings.Speakers.ExpectedSpeakers > 0
            ? _settings.Speakers.ExpectedSpeakers.ToString()
            : "Auto";

        // Each job step gets the settings as they are when it starts.
        _queue = new JobQueue(new JobStore(AppPaths.Recordings), () => new TranscriptionPipeline(_settings.Clone()));
        _queue.Changed += job => Dispatcher.BeginInvoke(() => ShowJob(job));
        _queue.Removed += id => Dispatcher.BeginInvoke(() => HideJob(id));

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(80), DispatcherPriority.Background, OnTick, Dispatcher);
        Loaded += (_, _) =>
        {
            SystemThemeWatcher.Watch(this);
            LoadDevices();
            _timer.Start();
            _queue.Start();
            foreach (var job in _queue.Jobs) ShowJob(job);
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
        else
        {
            foreach (var s in _sources) s.Level = Scale(s.ReadSystemMeter());
        }
    }

    /// <summary>Square root makes quiet speech visible on a linear bar.</summary>
    private static double Scale(float peak) => Math.Sqrt(Math.Clamp(peak, 0f, 1f));

    // ----- Recording -----------------------------------------------------------------------

    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Recording) await StopAndQueueAsync();
        else StartRecording();
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
            // The title is read again on Stop, so it can be typed or changed while recording.
            _session = RecordingSession.Start(FileNames.CleanTitle(TitleBox.Text), selections);
        }
        catch (Exception ex)
        {
            ShowStatus(Tone.Error, "Could not start recording.", ex.Message);
            return;
        }

        SetMode(Mode.Recording);
        ShowStatus(Tone.Busy, $"Recording {string.Join(", ", selections.Select(s => s.Label))}…",
            "Press Stop when you're done. It's transcribed in the background, so you can start the next recording right away.");
    }

    private async Task StopAndQueueAsync()
    {
        var session = _session!;
        _session = null;
        RecordButton.IsEnabled = false;
        var recording = await session.StopAsync();
        RecordButton.IsEnabled = true;
        SetMode(Mode.Idle);
        recording = recording with { Title = FileNames.CleanTitle(TitleBox.Text) };
        TitleBox.Text = "";

        Enqueue(recording);
        var dropped = session.Failures.Select(f => $"{f.Device} stopped early: {f.Error.Message}").ToList();
        if (dropped.Count > 0)
            ShowStatus(Tone.Warning, "Recording saved, but not every source made it to the end.", string.Join("\n", dropped));
        else
            StatusCard.Visibility = Visibility.Collapsed;
    }

    private void OnTranscribeFile(object sender, RoutedEventArgs e)
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

        if (!string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            recording = recording with { Title = FileNames.CleanTitle(TitleBox.Text) };
            TitleBox.Text = "";
        }
        Enqueue(recording);
    }

    /// <summary>The box asks for everyone on the call; the server is told how many to find on the split source.</summary>
    private void Enqueue(SessionRecording recording) =>
        _queue.Enqueue(recording, SpeakerCount.PerDiarizedSource(_settings.Speakers.ExpectedSpeakers, recording.Sources));

    // ----- Transcripts ---------------------------------------------------------------------

    private void ShowJob(TranscriptionJob job)
    {
        if (_jobs.FirstOrDefault(j => j.Id == job.Id) is { } item)
        {
            item.Job = job;
        }
        else
        {
            // Newest first, the same order the queue lists them in.
            int at = _jobs.TakeWhile(j => j.Job.CreatedAt > job.CreatedAt).Count();
            _jobs.Insert(at, new JobItem(job));
        }
        JobsHeader.Visibility = Visibility.Visible;
    }

    private void HideJob(string id)
    {
        if (_jobs.FirstOrDefault(j => j.Id == id) is { } item) _jobs.Remove(item);
        if (_jobs.Count == 0) JobsHeader.Visibility = Visibility.Collapsed;
    }

    private static JobItem ItemOf(object sender) => (JobItem)((FrameworkElement)sender).DataContext;

    private async void OnNameSpeakers(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (_queue.GetSummaries(item.Id) is not { } speakers) return;

        var dialog = new SpeakerReviewWindow(speakers) { Owner = this, Title = $"Who was speaking in “{item.Title}”?" };
        // Closing without choosing leaves the job waiting, to be named later.
        if (dialog.ShowDialog() == true && dialog.Names is { } names)
            await _queue.SubmitNamesAsync(item.Id, names);
    }

    private void OnOpenNote(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender).Job.NoteLocalPath is { } path) Shell(path);
    }

    private void OnShowJobFolder(object sender, RoutedEventArgs e)
    {
        var job = ItemOf(sender).Job;
        if (job.NoteLocalPath is { } note && File.Exists(note))
            Process.Start("explorer.exe", $"/select,\"{note}\"");
        else if (Directory.Exists(job.Recording.Directory))
            Shell(job.Recording.Directory);
        else
            ShowStatus(Tone.Warning, "That folder no longer exists.");
    }

    private void OnRetryJob(object sender, RoutedEventArgs e) => _queue.Retry(ItemOf(sender).Id);

    /// <summary>Asks for a new title; the note is named and headed with it when it's saved.</summary>
    private void OnRenameJob(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        var dialog = new RenameWindow(item.Job.Recording.Title) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!_queue.Rename(item.Id, dialog.NewTitle))
            ShowStatus(Tone.Warning, "The note was already being saved, so it kept its old name.");
    }

    /// <summary>Asks how many people were on the call, with what a new recording would use now ticked.</summary>
    private void OnReprocessJob(object sender, RoutedEventArgs e)
    {
        var id = ItemOf(sender).Id;
        int current = _settings.Speakers.ExpectedSpeakers;
        var menu = new System.Windows.Controls.ContextMenu
        {
            PlacementTarget = (UIElement)sender,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "People on the call (including you)", IsEnabled = false });
        foreach (var n in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = n switch { 0 => "Auto", 1 => "1 – don't separate", _ => $"{n} people" },
                IsChecked = n == current,
            };
            item.Click += (_, _) => _queue.ReprocessForPeople(id, n);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OnCancelJob(object sender, RoutedEventArgs e) => _queue.Cancel(ItemOf(sender).Id);

    private void OnRemoveJob(object sender, RoutedEventArgs e)
    {
        var item = ItemOf(sender);
        if (item.RemoveDeletesAudio)
        {
            var answer = System.Windows.MessageBox.Show(
                $"Delete the recording of “{item.Title}”? Its audio can't be recovered. Notes already saved are not affected.",
                "Transcriber", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }
        _queue.Remove(item.Id);
    }

    // ----- Status and state ----------------------------------------------------------------

    private void SetMode(Mode mode)
    {
        _mode = mode;
        bool idle = mode == Mode.Idle;
        SourcesList.IsEnabled = idle;
        RefreshButton.IsEnabled = idle;
        VoicesBox.IsEnabled = idle;

        RecordButton.Content = idle ? "Start recording" : "Stop and transcribe";
        RecordButton.Icon = new SymbolIcon(idle ? SymbolRegular.Record24 : SymbolRegular.Stop24);
        RecordButton.Appearance = idle ? ControlAppearance.Primary : ControlAppearance.Danger;

        if (idle)
        {
            ElapsedText.Text = "00:00:00";
            foreach (var s in _sources) s.Level = 0;
        }
    }

    private void ShowStatus(Tone tone, string text, string? detail = null)
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
        if (_mode == Mode.Recording)
        {
            var answer = System.Windows.MessageBox.Show(
                "A recording is in progress. Quit anyway? Audio recorded so far stays in the recordings folder.",
                "Transcriber", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _session?.Dispose();
        }
        else if (_queue.IsBusy)
        {
            var answer = System.Windows.MessageBox.Show(
                "Transcripts are still being made. Quit anyway? They'll pick up where they left off next time you open Transcriber.",
                "Transcriber", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _queue.Dispose();
        _timer.Stop();
        SavePreferences();
        foreach (var item in _sources) item.Dispose();
    }
}
