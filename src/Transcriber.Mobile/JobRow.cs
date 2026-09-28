using System.ComponentModel;
using Transcriber.Core.Jobs;
using Transcriber.Core.Output;

namespace Transcriber.Mobile;

/// <summary>One card in the transcript list. Replace <see cref="Job"/> and every binding refreshes.</summary>
public sealed class JobRow(TranscriptionJob job) : INotifyPropertyChanged
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

    public string Subtitle =>
        $"{_job.StateText} · {_job.Recording.StartedAt.LocalDateTime:ddd d MMM, HH:mm} · {MarkdownRenderer.Clock(_job.Recording.Duration)}";

    public string Detail => _job.Detail;

    public bool IsBusy => _job.State is JobState.Transcribing or JobState.Saving;

    public bool CanName => _job.CanName;

    public bool CanCancel => _job.CanCancel;

    public bool CanRetry => _job.CanRetry;

    public bool CanReprocess => _job.CanReprocess;

    public bool CanRemove => !_job.IsActive;

    public string RemoveText => _job.RemoveDeletesAudio ? "Delete" : "Dismiss";
}
