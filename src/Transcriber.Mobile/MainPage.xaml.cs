using System.Collections.ObjectModel;
using Transcriber.Core.Jobs;
using Transcriber.Core.Output;
using Transcriber.Core.Settings;

namespace Transcriber.Mobile;

/// <summary>
/// Microphone and speaker choices plus a view of the <see cref="SessionController"/> and its transcripts.
/// The page holds no recording state, so Android can destroy and recreate it mid-recording without losing anything.
/// </summary>
public partial class MainPage : ContentPage
{
    private const string MicPref = "mic.key";
    private const string LabelPref = "mic.label";
    private const string SplitPref = "mic.diarize";
    private static readonly string[] VoiceChoices = ["Auto", "2", "3", "4", "5", "6", "8"];

    private readonly SessionController _session = SessionController.Instance;
    private readonly IDispatcherTimer _timer;
    private readonly ObservableCollection<JobRow> _jobs = [];
    private AppSettings _settings = SettingsStore.Load();
    private IReadOnlyList<MicOption> _mics = [];
    private MicOption? _mic;
    private IDisposable? _deviceWatch;

    public MainPage()
    {
        InitializeComponent();
        VoicesPicker.ItemsSource = VoiceChoices;
        LabelEntry.Text = Preferences.Get(LabelPref, "");
        SplitSwitch.IsToggled = Preferences.Get(SplitPref, true);
        BindableLayout.SetItemsSource(JobList, _jobs);

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += OnTick;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _settings = SettingsStore.Load();
        var voices = _settings.Speakers.ExpectedSpeakers;
        VoicesPicker.SelectedIndex = Math.Max(0, Array.IndexOf(VoiceChoices, voices > 0 ? voices.ToString() : "Auto"));
        OnSplitToggled(this, new ToggledEventArgs(SplitSwitch.IsToggled));

        _deviceWatch ??= MicDevices.Watch(LoadMics);
        _session.EnsureStarted();
        _session.Changed += Render;
        _session.JobChanged += ShowJob;
        _session.JobRemoved += HideJob;
        _jobs.Clear();
        foreach (var job in _session.Queue.Jobs) ShowJob(job);
        _timer.Start();
        Render();

        if (_session.Mode == SessionMode.Idle && _session.Status is null)
        {
            if (_settings.Stt.BaseUrl == new SttSettings().BaseUrl)
                ShowStatus(new SessionStatus(Tone.Warning, "Set your speech-to-text server in Settings before recording."));
            else if (_settings.Output.Destination == OutputKind.MarkdownFolder && !_settings.Output.MarkdownFolder.StartsWith("content://"))
                ShowStatus(new SessionStatus(Tone.Warning, "Choose where notes are saved in Settings → Output."));
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _session.Changed -= Render;
        _session.JobChanged -= ShowJob;
        _session.JobRemoved -= HideJob;
        _timer.Stop();
    }

    // ----- Microphones ---------------------------------------------------------------------

    private void LoadMics()
    {
        if (_session.Mode != SessionMode.Idle) return;
        _mics = MicDevices.List();
        var wanted = _mic?.Key ?? Preferences.Get(MicPref, "phone");
        _mic = _mics.FirstOrDefault(m => m.Key == wanted) ?? _mics.FirstOrDefault();

        MicList.Children.Clear();
        foreach (var option in _mics)
        {
            var radio = new RadioButton { Content = option.Display, GroupName = "mic", Value = option, IsChecked = option == _mic };
            radio.CheckedChanged += (_, e) =>
            {
                if (!e.Value) return;
                _mic = option;
                Preferences.Set(MicPref, option.Key);
            };
            MicList.Children.Add(radio);
        }

        MicHint.Text = _mics.Any(m => m.Kind == MicKind.Bluetooth)
            ? "Bluetooth audio switches to its call-quality mic while recording."
            : "Bluetooth headsets show up here once they're connected for calls.";
    }

    private void OnSplitToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Set(SplitPref, e.Value);
        VoicesPicker.IsEnabled = e.Value && _session.Mode == SessionMode.Idle;
        LabelEntry.Placeholder = e.Value ? "Speaker" : "Me";
        LabelHint.Text = e.Value
            ? "Voices become Speaker 1, Speaker 2, … and you can name them before saving."
            : "Everything is attributed to this one name.";
    }

    private void OnVoicesChanged(object? sender, EventArgs e)
    {
        var n = int.TryParse(VoicesPicker.SelectedItem as string, out var v) ? v : 0;
        if (_settings.Speakers.ExpectedSpeakers == n) return;
        _settings.Speakers.ExpectedSpeakers = n;
        SettingsStore.Save(_settings);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_session.Mode != SessionMode.Recording) return;
        TimerLabel.Text = MarkdownRenderer.Clock(_session.Elapsed);
        LevelBar.Progress = Math.Sqrt(Math.Clamp(_session.ReadPeak(), 0f, 1f));
    }

    // ----- Actions -------------------------------------------------------------------------

    private async void OnRecord(object? sender, EventArgs e)
    {
        if (_session.Mode == SessionMode.Recording)
        {
            await _session.StopAndTranscribeAsync();
            return;
        }
        if (_session.Mode != SessionMode.Idle) return;

        if (_mic is null)
        {
            ShowStatus(new SessionStatus(Tone.Warning, "No microphone is available."));
            return;
        }
        if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
        {
            ShowStatus(new SessionStatus(Tone.Error, "Transcriber needs microphone access to record.",
                "Allow it in Android Settings → Apps → Transcriber → Permissions."));
            return;
        }
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>(); // for the Stop button and "saved" alerts

        var label = LabelEntry.Text?.Trim() ?? "";
        Preferences.Set(LabelPref, label);
        await _session.StartAsync(_mic, TitleEntry.Text?.Trim() ?? "", label, SplitSwitch.IsToggled);
        if (_session.Mode == SessionMode.Recording) TitleEntry.Text = "";
    }

    private async void OnSettings(object? sender, EventArgs e)
    {
        if (_session.Mode != SessionMode.Idle) return;
        await Navigation.PushAsync(new SettingsPage());
    }

    // ----- Transcripts ---------------------------------------------------------------------

    private void ShowJob(TranscriptionJob job)
    {
        if (_jobs.FirstOrDefault(j => j.Id == job.Id) is { } row)
        {
            row.Job = job;
        }
        else
        {
            // Newest first, the same order the queue lists them in.
            int at = _jobs.TakeWhile(j => j.Job.CreatedAt > job.CreatedAt).Count();
            _jobs.Insert(at, new JobRow(job));
        }
        JobsHeader.IsVisible = true;
    }

    private void HideJob(string id)
    {
        if (_jobs.FirstOrDefault(j => j.Id == id) is { } row) _jobs.Remove(row);
        JobsHeader.IsVisible = _jobs.Count > 0;
    }

    private static JobRow RowOf(object? sender) => (JobRow)((BindableObject)sender!).BindingContext;

    private async void OnNameSpeakers(object? sender, EventArgs e)
    {
        var row = RowOf(sender);
        if (_session.Queue.GetSummaries(row.Id) is not { } speakers) return;

        var page = new SpeakerReviewPage(speakers, row.Title);
        await Navigation.PushModalAsync(page);
        // Backing out leaves the transcript waiting, to be named later.
        if (await page.Result is { } names)
            await Task.Run(() => _session.Queue.SubmitNamesAsync(row.Id, names));
    }

    private void OnRetryJob(object? sender, EventArgs e) => _session.Queue.Retry(RowOf(sender).Id);

    private void OnReprocessJob(object? sender, EventArgs e) => _session.Queue.Reprocess(RowOf(sender).Id);

    /// <summary>Hands the kept audio to Android's share sheet (Drive, Quick Share, email…) to get it off the phone.</summary>
    private async void OnShareAudio(object? sender, EventArgs e)
    {
        var row = RowOf(sender);
        var files = row.Job.Recording.Sources.Select(s => s.FilePath).Where(File.Exists).Select(p => new ShareFile(p)).ToList();
        if (files.Count == 0)
        {
            await DisplayAlertAsync("No audio", $"The audio of “{row.Title}” is no longer on this phone.", "OK");
            return;
        }
        await Share.Default.RequestAsync(new ShareMultipleFilesRequest(row.Title, files));
    }

    private void OnCancelJob(object? sender, EventArgs e) => _session.Queue.Cancel(RowOf(sender).Id);

    private async void OnRemoveJob(object? sender, EventArgs e)
    {
        var row = RowOf(sender);
        if (row.Job.RemoveDeletesAudio && !await DisplayAlertAsync("Delete recording?",
                $"The audio of “{row.Title}” will be deleted and can't be recovered. Notes already saved are not affected.",
                "Delete", "Keep"))
            return;
        _session.Queue.Remove(row.Id);
    }

    // ----- Rendering -----------------------------------------------------------------------

    private void Render()
    {
        var mode = _session.Mode;
        bool idle = mode == SessionMode.Idle;
        MicList.IsEnabled = idle;
        SplitSwitch.IsEnabled = idle;
        VoicesPicker.IsEnabled = idle && SplitSwitch.IsToggled;
        LabelEntry.IsEnabled = idle;
        TitleEntry.IsEnabled = idle;
        LevelBar.IsVisible = !idle;

        RecordButton.Text = idle ? "Start recording" : "Stop and transcribe";
        RecordButton.BackgroundColor = idle ? Accent : Color.FromArgb("#DC2626");

        if (idle)
        {
            TimerLabel.Text = "00:00:00";
            LevelBar.Progress = 0;
            LoadMics();
        }
        if (_session.Status is { } status) ShowStatus(status);
        else StatusCard.IsVisible = false;
    }

    private static Color Accent =>
        Application.Current?.Resources.TryGetValue("Primary", out var c) == true && c is Color color ? color : Color.FromArgb("#2563EB");

    private void ShowStatus(SessionStatus status)
    {
        StatusCard.IsVisible = true;
        StatusText.Text = status.Text;
        StatusDetail.Text = status.Detail ?? "";
        StatusDetail.IsVisible = !string.IsNullOrWhiteSpace(status.Detail);
        Busy.IsRunning = Busy.IsVisible = status.Tone == Tone.Busy;
        StatusIcon.IsVisible = status.Tone != Tone.Busy;
        (StatusIcon.Text, StatusIcon.TextColor) = status.Tone switch
        {
            Tone.Success => ("✓", Color.FromArgb("#16A34A")),
            Tone.Warning => ("!", Color.FromArgb("#D97706")),
            Tone.Error => ("✕", Color.FromArgb("#DC2626")),
            _ => ("", Colors.Gray),
        };
    }
}
