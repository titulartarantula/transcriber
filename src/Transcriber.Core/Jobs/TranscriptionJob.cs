using System.Text.Json.Serialization;
using Transcriber.Core.Audio;
using Transcriber.Core.Pipeline;

namespace Transcriber.Core.Jobs;

public enum JobState { Queued, Transcribing, NeedsNames, Saving, Saved, Failed, Cancelled }

/// <summary>One recording on its way to a note. Saved as job.json in the recording's own folder.</summary>
/// <param name="Id">The recording folder's name.</param>
/// <param name="ExpectedSpeakers">Voices per diarized source chosen when it was recorded, or 0 to estimate.</param>
public sealed record TranscriptionJob(string Id, SessionRecording Recording, int ExpectedSpeakers, DateTimeOffset CreatedAt)
{
    public JobState State { get; init; } = JobState.Queued;

    /// <summary>What the job is doing right now; not saved.</summary>
    [JsonIgnore]
    public string? Progress { get; init; }

    public string? Error { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public StepTimings? Timings { get; init; }

    public string? NoteDescription { get; init; }

    public string? NoteLocalPath { get; init; }

    /// <summary>The note is saved and the 16 kHz audio was kept so the job can be reprocessed.</summary>
    public bool AudioKept { get; init; }

    [JsonIgnore]
    public string Title => string.IsNullOrWhiteSpace(Recording.Title) ? "Untitled recording" : Recording.Title.Trim();

    /// <summary>Queued or being worked on; it can be cancelled but not removed.</summary>
    [JsonIgnore]
    public bool IsActive => State is JobState.Queued or JobState.Transcribing or JobState.Saving;

    [JsonIgnore]
    public bool CanName => State == JobState.NeedsNames;

    [JsonIgnore]
    public bool CanCancel => State is JobState.Queued or JobState.Transcribing;

    [JsonIgnore]
    public bool CanRetry => State is JobState.Failed or JobState.Cancelled;

    [JsonIgnore]
    public bool CanReprocess => State == JobState.Saved && AudioKept;

    /// <summary>Removing deletes audio that can't be recovered, unless the note is saved and nothing was kept.</summary>
    [JsonIgnore]
    public bool RemoveDeletesAudio => !(State == JobState.Saved && !AudioKept);

    [JsonIgnore]
    public string StateText => State switch
    {
        JobState.Queued => "Waiting",
        JobState.Transcribing => "Transcribing",
        JobState.NeedsNames => "Needs names",
        JobState.Saving => "Saving",
        JobState.Saved => AudioKept ? "Saved · audio kept" : "Saved",
        JobState.Failed => "Failed",
        _ => "Cancelled",
    };

    /// <summary>What's happening or what went wrong, then warnings and timings once transcribed.</summary>
    [JsonIgnore]
    public string Detail
    {
        get
        {
            var lines = new List<string>
            {
                State switch
                {
                    JobState.Queued => "Starts when the transcript ahead of it is done.",
                    JobState.Transcribing or JobState.Saving => Progress ?? "Working…",
                    JobState.NeedsNames => "Transcribed. Name the speakers to save the note.",
                    JobState.Saved => $"Saved {NoteDescription}",
                    JobState.Failed => $"{Error ?? "Something went wrong."} The recording is kept, so you can retry.",
                    _ => "Cancelled. The recording is kept, so you can retry.",
                },
            };
            if (State is JobState.NeedsNames or JobState.Saved)
            {
                lines.AddRange(Warnings);
                if (Timings is { } t) lines.Add(t.Describe() + ".");
            }
            return string.Join("\n", lines);
        }
    }
}
