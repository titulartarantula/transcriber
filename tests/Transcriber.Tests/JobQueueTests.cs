using System.Collections.Concurrent;
using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;
using Transcriber.Core.Jobs;
using Transcriber.Core.Output;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;
using Transcriber.Core.Transcript;
using Xunit;

namespace Transcriber.Tests;

public sealed class JobQueueTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "transcriber-jobs-" + Guid.NewGuid());
    private readonly FakeRunner _runner = new();

    public JobQueueTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private JobQueue NewQueue()
    {
        var queue = new JobQueue(new JobStore(_root), () => _runner);
        queue.Start();
        return queue;
    }

    private SessionRecording Record(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var raw = Path.Combine(dir, "source1-SystemAudio.wav");
        File.WriteAllText(raw, "raw");
        File.WriteAllText(Path.Combine(dir, "source1-SystemAudio-16k.wav"), "16k");
        return new SessionRecording(name, DateTimeOffset.Now, TimeSpan.FromMinutes(1), dir,
            [new RecordedSource(raw, "Remote", SourceKind.SystemAudio, true, "Speakers")]);
    }

    private static async Task<TranscriptionJob> WaitFor(JobQueue queue, string id, Func<TranscriptionJob, bool> done)
    {
        for (int i = 0; i < 500; i++)
        {
            if (queue.Find(id) is { } job && done(job)) return job;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Job {id} is {queue.Find(id)?.State}.");
    }

    [Fact]
    public async Task A_job_waiting_for_names_does_not_hold_up_the_next()
    {
        using var queue = NewQueue();
        var first = queue.Enqueue(Record("a"), 0);
        await Task.Delay(5); // distinct CreatedAt so the order is defined
        var second = queue.Enqueue(Record("b"), 2);

        await WaitFor(queue, first.Id, j => j.State == JobState.NeedsNames);
        await WaitFor(queue, second.Id, j => j.State == JobState.NeedsNames);

        Assert.Equal(["a", "b"], _runner.Transcribed.Select(r => r.Title));
        Assert.Equal(2, _runner.ExpectedSpeakers[1]);
        Assert.Contains(queue.GetSummaries(first.Id)!, s => s.IsGeneric);
    }

    [Fact]
    public async Task Naming_saves_the_note_and_deletes_the_recording_when_audio_is_not_kept()
    {
        using var queue = NewQueue();
        var job = queue.Enqueue(Record("a"), 0);
        await WaitFor(queue, job.Id, j => j.State == JobState.NeedsNames);

        await queue.SubmitNamesAsync(job.Id, new Dictionary<string, string> { ["Remote 1"] = "Priya" });

        var saved = queue.Find(job.Id)!;
        Assert.Equal(JobState.Saved, saved.State);
        Assert.Equal("note a", saved.NoteDescription);
        Assert.Equal("Priya", _runner.Names.Single()!["Remote 1"]);
        Assert.False(Directory.Exists(job.Recording.Directory));
    }

    [Fact]
    public async Task Kept_audio_is_the_16k_copy_and_can_be_reprocessed()
    {
        _runner.Settings.Speakers.ReviewNames = false;
        _runner.Settings.Output.KeepAudio = true;
        using var queue = NewQueue();
        var job = queue.Enqueue(Record("a"), 0);

        var saved = await WaitFor(queue, job.Id, j => j.State == JobState.Saved);
        Assert.True(saved.AudioKept);
        var source = Assert.Single(saved.Recording.Sources);
        Assert.EndsWith("source1-SystemAudio-16k.wav", source.FilePath);
        Assert.False(File.Exists(Path.Combine(job.Recording.Directory, "source1-SystemAudio.wav")));
        Assert.True(File.Exists(Path.Combine(job.Recording.Directory, "job.json")));

        // Turning the setting off later doesn't throw away audio that was deliberately kept.
        _runner.Settings.Output.KeepAudio = false;
        queue.Reprocess(job.Id);
        var again = await WaitFor(queue, job.Id, j => j.State == JobState.Saved && _runner.Transcribed.Count == 2);
        Assert.Equal(source.FilePath, _runner.Transcribed[1].Sources.Single().FilePath);
        Assert.True(again.AudioKept);
        Assert.True(Directory.Exists(job.Recording.Directory));
    }

    [Fact]
    public async Task Reprocess_can_change_how_many_voices_to_separate()
    {
        _runner.Settings.Speakers.ReviewNames = false;
        _runner.Settings.Output.KeepAudio = true;
        using var queue = NewQueue();
        var job = queue.Enqueue(Record("a"), 0);
        await WaitFor(queue, job.Id, j => j.State == JobState.Saved);

        queue.Reprocess(job.Id, 2);
        var again = await WaitFor(queue, job.Id, j => j.State == JobState.Saved && _runner.Transcribed.Count == 2);
        Assert.Equal(2, _runner.ExpectedSpeakers[1]);
        Assert.Equal(2, again.ExpectedSpeakers);

        queue.Reprocess(job.Id, 1);
        await WaitFor(queue, job.Id, j => j.State == JobState.Saved && _runner.Transcribed.Count == 3);
        Assert.False(_runner.Transcribed[2].Sources.Single().Diarize);
    }

    [Fact]
    public void Separating_voices_turns_it_on_for_a_recording_made_without()
    {
        var recording = new SessionRecording("t", DateTimeOffset.Now, TimeSpan.FromMinutes(1), "d",
            [new RecordedSource("mic.aac", "Me", SourceKind.Microphone, false, "Mic")]);
        var job = JobQueue.WithVoices(new TranscriptionJob("a", recording, 0, DateTimeOffset.Now), 3);
        Assert.True(job.Recording.Sources.Single().Diarize);
        Assert.Equal(3, job.ExpectedSpeakers);
    }

    [Fact]
    public async Task Cancelled_job_can_be_retried()
    {
        _runner.Block = true;
        using var queue = NewQueue();
        var job = queue.Enqueue(Record("a"), 0);
        await WaitFor(queue, job.Id, j => j.State == JobState.Transcribing);

        queue.Cancel(job.Id);
        await WaitFor(queue, job.Id, j => j.State == JobState.Cancelled);

        _runner.Block = false;
        queue.Retry(job.Id);
        await WaitFor(queue, job.Id, j => j.State == JobState.NeedsNames);
    }

    [Fact]
    public async Task Interrupted_work_resumes_after_a_restart()
    {
        var store = new JobStore(_root);
        var recording = Record("a");
        store.Save(new TranscriptionJob("a", recording, 0, DateTimeOffset.Now) { State = JobState.Transcribing });

        using var queue = NewQueue();
        await WaitFor(queue, "a", j => j.State == JobState.NeedsNames);
    }

    [Fact]
    public async Task A_failed_save_retries_from_the_transcript_without_transcribing_again()
    {
        _runner.Settings.Speakers.ReviewNames = false;
        _runner.FailFinish = true;
        using var queue = NewQueue();
        var job = queue.Enqueue(Record("a"), 0);
        var failed = await WaitFor(queue, job.Id, j => j.State == JobState.Failed);
        Assert.Equal("disk full", failed.Error);

        _runner.FailFinish = false;
        queue.Retry(job.Id);
        await WaitFor(queue, job.Id, j => j.State == JobState.Saved);
        Assert.Single(_runner.Transcribed);
    }

    [Fact]
    public async Task Removing_a_job_deletes_its_folder()
    {
        using var queue = NewQueue();
        var job = queue.Enqueue(Record("a"), 0);
        await WaitFor(queue, job.Id, j => j.State == JobState.NeedsNames);

        queue.Remove(job.Id);

        Assert.Null(queue.Find(job.Id));
        Assert.False(Directory.Exists(job.Recording.Directory));
    }

    [Fact]
    public void Draft_survives_a_round_trip_through_disk()
    {
        var store = new JobStore(_root);
        var job = new TranscriptionJob("a", Record("a"), 0, DateTimeOffset.Now);
        var draft = FakeRunner.Draft();
        store.SaveDraft(job, draft);

        var loaded = store.LoadDraft(job)!;
        var pipeline = new TranscriptionPipeline(new AppSettings());
        Assert.Equal(pipeline.Summarize(draft), pipeline.Summarize(loaded));
        Assert.Equal(draft.Timings, loaded.Timings);
    }

    private sealed class FakeRunner : IJobRunner
    {
        public AppSettings Settings { get; } = new();

        public ConcurrentQueue<SessionRecording> TranscribedQueue { get; } = new();

        public List<SessionRecording> Transcribed => TranscribedQueue.ToList();

        public List<int> ExpectedSpeakers { get; } = [];

        public List<IReadOnlyDictionary<string, string>?> Names { get; } = [];

        public volatile bool Block;

        public volatile bool FailFinish;

        public static TranscriptDraft Draft()
        {
            var words = new List<TimedWord> { new(0, 0.5, " Hi,"), new(0.5, 1, " I'm"), new(1, 1.5, " Priya."), new(3, 3.5, " Hello.") };
            var turns = new List<SpeakerTurn> { new(0, 2, 0), new(2.5, 4, 1) };
            return new TranscriptDraft(
                [new SourceTranscript("Remote", SourceKind.SystemAudio, words, turns)],
                "en", "model", ["a warning"], new StepTimings(12, 3, "on the server"));
        }

        public async Task<TranscriptDraft> TranscribeAsync(SessionRecording recording, int expectedSpeakers,
            IProgress<string> progress, CancellationToken ct)
        {
            progress.Report("Transcribing…");
            while (Block) await Task.Delay(10, ct);
            TranscribedQueue.Enqueue(recording);
            lock (ExpectedSpeakers) ExpectedSpeakers.Add(expectedSpeakers);
            return Draft();
        }

        public IReadOnlyList<SpeakerSummary> Summarize(TranscriptDraft draft) => new TranscriptionPipeline(Settings).Summarize(draft);

        public Task<PipelineResult> FinishAsync(SessionRecording recording, TranscriptDraft draft,
            IReadOnlyDictionary<string, string>? names, IProgress<string> progress, CancellationToken ct)
        {
            if (FailFinish) throw new IOException("disk full");
            Names.Add(names);
            return Task.FromResult(new PipelineResult(new SavedNote("note " + recording.Title, null), draft.Warnings, 2));
        }
    }
}
