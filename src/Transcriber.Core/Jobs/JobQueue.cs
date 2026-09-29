using Transcriber.Core.Audio;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;
using Transcriber.Core.Transcript;

namespace Transcriber.Core.Jobs;

/// <summary>The parts of <see cref="TranscriptionPipeline"/> the queue uses, so tests can stand in for it.</summary>
public interface IJobRunner
{
    AppSettings Settings { get; }

    Task<TranscriptDraft> TranscribeAsync(SessionRecording recording, int expectedSpeakers, IProgress<string> progress, CancellationToken ct);

    IReadOnlyList<SpeakerSummary> Summarize(TranscriptDraft draft);

    Task<PipelineResult> FinishAsync(SessionRecording recording, TranscriptDraft draft,
        IReadOnlyDictionary<string, string>? names, IProgress<string> progress, CancellationToken ct);
}

/// <summary>
/// Transcribes recordings one at a time in the background while new ones are being recorded. A job whose
/// speakers need names waits in <see cref="JobState.NeedsNames"/> without holding up the next one. Jobs
/// live on disk next to their audio, so work interrupted by the app closing picks up again on the next start.
/// </summary>
public sealed class JobQueue : IDisposable
{
    private readonly JobStore _store;
    private readonly Func<IJobRunner> _runners;
    private readonly object _gate = new();
    private readonly Dictionary<string, TranscriptionJob> _jobs = [];
    private readonly Dictionary<string, CancellationTokenSource> _cancels = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly SemaphoreSlim _saving = new(1, 1);
    private readonly CancellationTokenSource _stop = new();

    /// <param name="runners">Makes a runner with the current settings; called once per step so edits apply to the next job.</param>
    public JobQueue(JobStore store, Func<IJobRunner> runners)
    {
        _store = store;
        _runners = runners;
    }

    /// <summary>A job was added or changed. Raised on whichever thread made the change, often a background one.</summary>
    public event Action<TranscriptionJob>? Changed;

    /// <summary>A job left the list. Raised on the caller's thread.</summary>
    public event Action<string>? Removed;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<TranscriptionJob> Jobs
    {
        get { lock (_gate) return _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList(); }
    }

    /// <summary>Some job is queued, transcribing or saving.</summary>
    public bool IsBusy
    {
        get { lock (_gate) return _jobs.Values.Any(j => j.IsActive); }
    }

    public TranscriptionJob? Find(string id)
    {
        lock (_gate) return _jobs.GetValueOrDefault(id);
    }

    /// <summary>Loads the saved jobs and starts working through the queue.</summary>
    public void Start()
    {
        foreach (var job in _store.LoadAll())
        {
            var resumed = job.State switch
            {
                JobState.Transcribing => job with { State = JobState.Queued },
                // The note may or may not have been written. Asking again beats silently writing a second copy.
                JobState.Saving => _store.LoadDraft(job) is null
                    ? job with { State = JobState.Failed, Error = "Saving was interrupted." }
                    : job with { State = JobState.NeedsNames },
                _ => job,
            };
            lock (_gate) _jobs[resumed.Id] = resumed;
        }
        _ = Task.Run(() => WorkAsync(_stop.Token));
    }

    public TranscriptionJob Enqueue(SessionRecording recording, int expectedSpeakers)
    {
        var job = new TranscriptionJob(Path.GetFileName(recording.Directory), recording, expectedSpeakers, DateTimeOffset.Now);
        lock (_gate)
        {
            _jobs[job.Id] = job;
            Persist(job);
        }
        Changed?.Invoke(job);
        _wake.Release();
        return job;
    }

    public void Cancel(string id)
    {
        lock (_gate)
        {
            if (_cancels.TryGetValue(id, out var running))
            {
                running.Cancel();
                return;
            }
        }
        Update(id, j => j.State == JobState.Queued ? j with { State = JobState.Cancelled } : j);
    }

    /// <summary>Tries a failed or cancelled job again, from the naming step if its transcript survived.</summary>
    public void Retry(string id)
    {
        if (Find(id) is not { State: JobState.Failed or JobState.Cancelled } job) return;

        if (_store.LoadDraft(job) is { } draft)
        {
            _ = AfterTranscribeAsync(id, draft, _runners());
            return;
        }
        Update(id, j => j with { State = JobState.Queued, Error = null });
        _wake.Release();
    }

    /// <summary>Transcribes a saved job's kept audio again with the current settings. The earlier note stays.</summary>
    public void Reprocess(string id)
    {
        if (Find(id) is not { State: JobState.Saved, AudioKept: true } job) return;

        Quietly(() => _store.DeleteDraft(job));
        Update(id, j => j with
        {
            State = JobState.Queued,
            Error = null,
            Warnings = [],
            Timings = null,
            NoteDescription = null,
            NoteLocalPath = null,
        });
        _wake.Release();
    }

    /// <summary>The speakers of a job waiting for names, or null if it isn't.</summary>
    public IReadOnlyList<SpeakerSummary>? GetSummaries(string id)
    {
        if (Find(id) is not { State: JobState.NeedsNames } job) return null;
        return _store.LoadDraft(job) is { } draft ? _runners().Summarize(draft) : null;
    }

    /// <param name="names">Speaker label → name. An empty map keeps the automatic labels.</param>
    public Task SubmitNamesAsync(string id, IReadOnlyDictionary<string, string> names)
    {
        if (Find(id) is not { State: JobState.NeedsNames } job) return Task.CompletedTask;

        if (_store.LoadDraft(job) is not { } draft)
        {
            Update(id, j => j with { State = JobState.Failed, Error = "The transcript for this recording is missing." });
            return Task.CompletedTask;
        }
        return SaveNoteAsync(id, draft, names, _runners());
    }

    /// <summary>Deletes a job that isn't running, together with its recording folder and any kept audio.</summary>
    public void Remove(string id)
    {
        TranscriptionJob? job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job) || job.IsActive) return;
            _jobs.Remove(id);
        }
        DeleteFolder(job.Recording.Directory);
        Removed?.Invoke(id);
    }

    /// <summary>Stops the worker. Unfinished jobs stay on disk and resume on the next <see cref="Start"/>.</summary>
    public void Dispose() => _stop.Cancel();

    private async Task WorkAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (TakeNext() is { } next)
            {
                await TranscribeAsync(next.Job, next.Cancel);
                continue;
            }
            try
            {
                await _wake.WaitAsync(stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private (TranscriptionJob Job, CancellationTokenSource Cancel)? TakeNext()
    {
        TranscriptionJob job;
        CancellationTokenSource cancel;
        lock (_gate)
        {
            var next = _jobs.Values.Where(j => j.State == JobState.Queued).MinBy(j => j.CreatedAt);
            if (next is null) return null;
            job = next with { State = JobState.Transcribing, Progress = "Starting…", Error = null };
            _jobs[job.Id] = job;
            Persist(job);
            cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _cancels[job.Id] = cancel;
        }
        Changed?.Invoke(job);
        return (job, cancel);
    }

    private async Task TranscribeAsync(TranscriptionJob job, CancellationTokenSource cancel)
    {
        var runner = _runners();
        TranscriptDraft? draft = null;
        try
        {
            var progress = new Reporter(m => Update(job.Id, j => j with { Progress = m }, persist: false));
            draft = await runner.TranscribeAsync(job.Recording, job.ExpectedSpeakers, progress, cancel.Token);
            _store.SaveDraft(job, draft);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // The app is closing. The job is still marked Transcribing on disk, so it resumes next time.
            return;
        }
        catch (OperationCanceledException)
        {
            Update(job.Id, j => j with { State = JobState.Cancelled, Progress = null });
        }
        catch (Exception e)
        {
            Update(job.Id, j => j with { State = JobState.Failed, Progress = null, Error = e.Message });
        }
        finally
        {
            lock (_gate) _cancels.Remove(job.Id);
            cancel.Dispose();
        }

        if (draft is not null) await AfterTranscribeAsync(job.Id, draft, runner);
    }

    /// <summary>Waits for names if there are anonymous voices to name, otherwise saves the note straight away.</summary>
    private async Task AfterTranscribeAsync(string id, TranscriptDraft draft, IJobRunner runner)
    {
        Update(id, j => j with
        {
            State = JobState.NeedsNames,
            Progress = null,
            Error = null,
            Warnings = draft.Warnings,
            Timings = draft.Timings,
        });

        bool ask = runner.Settings.Speakers.ReviewNames && runner.Summarize(draft).Any(s => s.IsGeneric);
        if (!ask) await SaveNoteAsync(id, draft, null, runner);
    }

    private async Task SaveNoteAsync(string id, TranscriptDraft draft, IReadOnlyDictionary<string, string>? names, IJobRunner runner)
    {
        TranscriptionJob? job = null;
        Update(id, j => j.State == JobState.NeedsNames ? job = j with { State = JobState.Saving, Progress = "Saving note…" } : j);
        if (job is null) return;

        await _saving.WaitAsync();
        try
        {
            var progress = new Reporter(m => Update(id, j => j with { Progress = m }, persist: false));
            var result = await runner.FinishAsync(job.Recording, draft, names, progress, _stop.Token);
            var saved = job with
            {
                State = JobState.Saved,
                Progress = null,
                Error = null,
                Warnings = result.Warnings,
                NoteDescription = result.Note.Description,
                NoteLocalPath = result.Note.LocalPath,
            };

            if (runner.Settings.Output.KeepAudio || job.AudioKept)
            {
                saved = KeepOnlyCompact(saved) with { AudioKept = true };
                Quietly(() => _store.DeleteDraft(saved));
                Update(id, _ => saved);
            }
            else
            {
                DeleteFolder(job.Recording.Directory);
                Update(id, _ => saved, persist: false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Closing mid-save: it comes back as NeedsNames on the next start.
        }
        catch (Exception e)
        {
            Update(id, j => j with { State = JobState.Failed, Progress = null, Error = e.Message });
        }
        finally
        {
            _saving.Release();
        }
    }

    /// <summary>
    /// Points each source at its compact copy (see <see cref="TranscriptionPipeline.CompactCopy"/>), which
    /// is all a reprocess needs, and deletes the original capture when it's one of ours. An imported file
    /// that was compact already is copied in, so the job doesn't depend on it staying where it was.
    /// </summary>
    private static TranscriptionJob KeepOnlyCompact(TranscriptionJob job)
    {
        var directory = job.Recording.Directory;
        var kept = new List<RecordedSource>();
        foreach (var source in job.Recording.Sources)
        {
            var keep = TranscriptionPipeline.CompactCopy(directory, source.FilePath);
            if (keep is not null && !IsInside(keep, directory))
            {
                var copy = Path.Combine(directory, Path.GetFileName(keep));
                Quietly(() => File.Copy(keep, copy, overwrite: true));
                keep = File.Exists(copy) ? copy : null;
            }
            if (IsInside(source.FilePath, directory) && (keep is null || !TranscriptionPipeline.SamePath(keep, source.FilePath)))
                Quietly(() => File.Delete(source.FilePath));
            // A silent source never got a copy; there's nothing to reprocess for it.
            if (keep is not null) kept.Add(source with { FilePath = keep });
        }
        return job with { Recording = job.Recording with { Sources = kept } };
    }

    private void Update(string id, Func<TranscriptionJob, TranscriptionJob> change, bool persist = true)
    {
        TranscriptionJob updated;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var current)) return;
            updated = change(current);
            if (ReferenceEquals(updated, current)) return;
            _jobs[id] = updated;
            if (persist) Persist(updated);
        }
        Changed?.Invoke(updated);
    }

    private void Persist(TranscriptionJob job)
    {
        if (Directory.Exists(job.Recording.Directory)) Quietly(() => _store.Save(job));
    }

    private void DeleteFolder(string directory)
    {
        if (IsInside(directory, _store.Root)) Quietly(() => Directory.Delete(directory, recursive: true));
    }

    private static bool IsInside(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File housekeeping that may fail (a locked file, a folder already gone) without losing the job.</summary>
    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Reports synchronously; <see cref="Progress{T}"/> would post to a thread pool and reorder messages.</summary>
    private sealed class Reporter(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
