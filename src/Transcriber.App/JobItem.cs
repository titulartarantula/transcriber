using System.ComponentModel;
using Transcriber.Core.Jobs;
using Transcriber.Core.Output;
using Wpf.Ui.Controls;

namespace Transcriber.App;

/// <summary>One row in the transcript list. Replace <see cref="Job"/> and every binding refreshes.</summary>
public sealed class JobItem(TranscriptionJob job) : INotifyPropertyChanged
{
    private TranscriptionJob _job = job;

    public event PropertyChangedEventHandler? PropertyChanged;

    public TranscriptionJob Job
    {
        get => _job;
        set
        {
            _job = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    public string Id => _job.Id;

    public string Title => _job.Title;

    public string When =>
        $"{_job.Recording.StartedAt.LocalDateTime:ddd d MMM, HH:mm} · {MarkdownRenderer.Clock(_job.Recording.Duration)}";

    public string StateText => _job.StateText;

    public string Detail => _job.Detail;

    public bool IsBusy => _job.State is JobState.Transcribing or JobState.Saving;

    public bool ShowIcon => !IsBusy;

    public SymbolRegular Icon => _job.State switch
    {
        JobState.Queued => SymbolRegular.Clock24,
        JobState.NeedsNames => SymbolRegular.People24,
        JobState.Saved => SymbolRegular.CheckmarkCircle24,
        JobState.Failed => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.DismissCircle24,
    };

    public bool CanName => _job.CanName;

    public bool CanCancel => _job.CanCancel;

    public bool CanRetry => _job.CanRetry;

    public bool CanOpen => _job.State == JobState.Saved && File.Exists(_job.NoteLocalPath);

    public bool CanReprocess => _job.CanReprocess;

    public bool CanRemove => !_job.IsActive;

    public bool RemoveDeletesAudio => _job.RemoveDeletesAudio;

    public string RemoveText => RemoveDeletesAudio ? "Delete" : "Dismiss";
}
