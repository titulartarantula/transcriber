using System.Diagnostics;
using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;
using Transcriber.Core.Jobs;
using Transcriber.Core.Output;
using Transcriber.Core.Settings;
using Transcriber.Core.Stt;
using Transcriber.Core.Transcript;

namespace Transcriber.Core.Pipeline;

public sealed record PipelineResult(SavedNote Note, IReadOnlyList<string> Warnings, int UtteranceCount);

/// <summary>Speaker label → chosen display name. Return null to keep the automatic labels.</summary>
public delegate Task<IReadOnlyDictionary<string, string>?> SpeakerReview(
    IReadOnlyList<SpeakerSummary> speakers, CancellationToken ct);

/// <summary>What the pipeline needs from a recording to write its note later: every source's words and turns.</summary>
public sealed record TranscriptDraft(
    IReadOnlyList<SourceTranscript> Sources,
    string? Language,
    string Model,
    IReadOnlyList<string> Warnings,
    StepTimings Timings);

/// <summary>How long transcription and voice separation took, summed over sources, and where voices were separated.</summary>
public sealed record StepTimings(double TranscribeSeconds, double DiarizeSeconds, string? DiarizedOn)
{
    public string Describe()
    {
        var text = $"Transcribed in {Duration(TranscribeSeconds)}";
        return DiarizedOn is null ? text : $"{text}, voices separated {DiarizedOn} in {Duration(DiarizeSeconds)}";
    }

    private static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:0} s" : $"{(int)(seconds / 60)} min {seconds % 60:0} s";
}

/// <summary>
/// Recording → per-source STT and diarization (<see cref="TranscribeAsync"/>) → merged, speaker-labelled
/// note → destination (<see cref="FinishAsync"/>). The two halves can run apart, with naming in between.
/// </summary>
public sealed class TranscriptionPipeline(AppSettings settings, SpeakerReview? review = null) : IJobRunner
{
    public AppSettings Settings => settings;

    /// <summary>Both halves back to back, asking <c>review</c> for names in between.</summary>
    public async Task<PipelineResult> RunAsync(SessionRecording recording, IProgress<string> progress, CancellationToken ct)
    {
        var draft = await TranscribeAsync(recording, settings.Speakers.ExpectedSpeakers, progress, ct);

        IReadOnlyDictionary<string, string>? names = null;
        var summaries = Summarize(draft);
        if (summaries.Count > 0 && settings.Speakers.ReviewNames && review is not null)
            names = await review(summaries, ct) ?? new Dictionary<string, string>();

        var result = await FinishAsync(recording, draft, names, progress, ct);
        DeleteWorkDirectory(recording.Directory);
        return result;
    }

    /// <param name="expectedSpeakers">Voices per diarized source, or 0 to estimate.</param>
    public async Task<TranscriptDraft> TranscribeAsync(
        SessionRecording recording, int expectedSpeakers, IProgress<string> progress, CancellationToken ct)
    {
        var warnings = new List<string>();
        var clock = new StepClock();
        bool diarize = settings.Speakers.Diarize && recording.Sources.Any(s => s.Diarize);

        using var stt = new WhisperClient(settings.Stt);
        var sources = new List<SourceTranscript>();
        string? language = null;

        foreach (var source in recording.Sources)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report($"Preparing {source.Label}…");
            var samples = await Task.Run(() => AudioConvert.LoadMono16k(source.FilePath), ct);
            if (AudioConvert.IsSilent(samples))
            {
                warnings.Add($"{source.Label} ({source.DeviceName}) was silent and was skipped.");
                continue;
            }

            var upload = await PrepareUploadAsync(recording.Directory, source, samples, progress, ct);

            bool separate = diarize && source.Diarize;
            progress.Report(separate
                ? $"Transcribing {source.Label} and separating speakers…"
                : $"Transcribing {source.Label}…");

            var (result, turns) = await TranscribeAndDiarize(
                stt, upload, samples, separate, expectedSpeakers, source.Label, warnings, clock, progress, ct);
            language ??= result.Language;
            sources.Add(new SourceTranscript(source.Label, source.Kind, WordExtractor.Extract(result), turns));
        }

        return new TranscriptDraft(sources, language, settings.Stt.Model, warnings, clock.ToTimings());
    }

    /// <summary>Every speaker with talk time, a sample line and a suggested name, for the naming step.</summary>
    public IReadOnlyList<SpeakerSummary> Summarize(TranscriptDraft draft)
    {
        var (merged, generic) = Build(draft);
        return SpeakerNames.Summarize(merged, generic);
    }

    /// <param name="names">
    /// Speaker label → display name. Null applies the suggested names; an empty map keeps the automatic labels.
    /// </param>
    public async Task<PipelineResult> FinishAsync(SessionRecording recording, TranscriptDraft draft,
        IReadOnlyDictionary<string, string>? names, IProgress<string> progress, CancellationToken ct)
    {
        var warnings = draft.Warnings.ToList();
        var (merged, generic) = Build(draft);
        names ??= SpeakerNames.Summarize(merged, generic)
            .Where(s => s.SuggestedName is not null)
            .ToDictionary(s => s.Speaker, s => s.SuggestedName!);
        merged = ApplySpeakerNames(merged, names);

        var title = string.IsNullOrWhiteSpace(recording.Title) ? "Transcript" : recording.Title.Trim();
        var note = new NoteData(
            title,
            recording.StartedAt,
            recording.Duration,
            recording.Sources.Select(s => new NoteSource(s.Label, s.DeviceName)).ToList(),
            merged,
            draft.Model,
            draft.Language,
            ParseTags(settings.Output.Tags),
            settings.Output.LinkSpeakers)
        {
            App = ProductInfo.Client,
        };

        var markdown = MarkdownRenderer.Render(note);
        var fileName = FileNames.Build(settings.Output.FileNameTemplate, title, recording.StartedAt);

        progress.Report("Saving note…");
        var saved = await Save(fileName, markdown, warnings, ct);
        return new PipelineResult(saved, warnings, merged.Count);
    }

    /// <summary>
    /// The file sent to the server, and kept afterwards if audio is kept: the source itself when it's
    /// already compact (a phone recording, kept audio being reprocessed), otherwise a compressed copy next
    /// to it, or a 16 kHz WAV where there's no encoder.
    /// </summary>
    private static async Task<string> PrepareUploadAsync(
        string directory, RecordedSource source, float[] samples, IProgress<string> progress, CancellationToken ct)
    {
        var sourcePath = source.FilePath;
        var duration = TimeSpan.FromSeconds(samples.Length / (double)AudioConvert.SampleRate);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        if (AudioConvert.IsCompact(sourcePath, duration) || name.EndsWith(Wav16Suffix, StringComparison.OrdinalIgnoreCase))
            return sourcePath;

        progress.Report($"Compressing {source.Label}…");
        if (AudioConvert.Encoder is { } encode)
            return await Task.Run(() => encode(sourcePath, Path.Combine(directory, name + CompactSuffix)), ct);

        var wav16 = Path.Combine(directory, name + Wav16Suffix + ".wav");
        await Task.Run(() => AudioConvert.WriteWav16(wav16, samples), ct);
        return wav16;
    }

    private const string CompactSuffix = "-compact";
    private const string Wav16Suffix = "-16k";

    /// <summary>
    /// The file worth keeping for a source once its note is saved: the copy <see cref="TranscribeAsync"/>
    /// made of it, else the source itself if it's compressed. Null if there's neither (a silent source).
    /// </summary>
    public static string? CompactCopy(string directory, string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var made = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, name + CompactSuffix + ".*")
                .Append(Path.Combine(directory, name + Wav16Suffix + ".wav"))
                .FirstOrDefault(File.Exists)
            : null;
        if (made is not null) return made;
        return File.Exists(sourcePath) && (AudioConvert.IsCompressed(sourcePath) || name.EndsWith(Wav16Suffix, StringComparison.OrdinalIgnoreCase))
            ? sourcePath
            : null;
    }

    internal static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private (List<Utterance> Merged, HashSet<string> Generic) Build(TranscriptDraft draft)
    {
        var generic = new HashSet<string>();
        var perSource = new List<List<Utterance>>();
        foreach (var source in draft.Sources)
        {
            var utterances = TranscriptBuilder.BuildForSource(source);
            if (source.Turns is not null) generic.UnionWith(utterances.Select(u => u.Speaker));
            perSource.Add(utterances);
        }
        return (TranscriptBuilder.Merge(perSource, settings.Speakers.SuppressEcho), generic);
    }

    private async Task<(WhisperResult, IReadOnlyList<SpeakerTurn>?)> TranscribeAndDiarize(
        WhisperClient stt, string upload, float[] samples, bool separate, int expectedSpeakers, string label,
        List<string> warnings, StepClock clock, IProgress<string> progress, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // STT and diarization don't depend on each other, so run them side by side.
        var diarization = separate
            ? Diarize(stt, upload, samples, expectedSpeakers, label, warnings, clock, progress, linked.Token)
            : Task.FromResult<IReadOnlyList<SpeakerTurn>?>(null);

        WhisperResult result;
        var watch = Stopwatch.StartNew();
        try
        {
            result = await stt.TranscribeAsync(upload, ct);
            clock.Transcribe += watch.Elapsed;
        }
        catch
        {
            linked.Cancel();
            try { await diarization; } catch { /* the STT failure is the one worth reporting */ }
            throw;
        }

        return (result, await diarization);
    }

    /// <summary>On the server when it can, else on this device; null if neither worked.</summary>
    private async Task<IReadOnlyList<SpeakerTurn>?> Diarize(WhisperClient stt, string upload, float[] samples,
        int expectedSpeakers, string label, List<string> warnings, StepClock clock, IProgress<string> progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (settings.Speakers.UseServer)
        {
            try
            {
                var turns = await stt.DiarizeAsync(upload, expectedSpeakers, ct);
                clock.Diarized(watch.Elapsed, "on the server");
                return turns;
            }
            catch (DiarizationUnsupportedException)
            {
                // A plain Whisper server: this device does it, as it always has.
            }
            catch (SttException e)
            {
                warnings.Add($"The server couldn't separate voices in {label}, so this device did ({e.Message}).");
            }
            watch.Restart();
        }

        try
        {
            await DiarizationModels.EnsureAsync(progress, ct);
            var turns = await Task.Run(
                () => Diarizer.Run(samples, expectedSpeakers, settings.Speakers.ClusterThreshold, ct: ct), ct);
            clock.Diarized(watch.Elapsed, "on this device");
            return turns;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            warnings.Add($"Speaker separation failed for {label}; its lines are labelled \"{label}\" ({e.Message}).");
            return null;
        }
    }

    private static List<Utterance> ApplySpeakerNames(List<Utterance> utterances, IReadOnlyDictionary<string, string> names)
    {
        if (names.Count == 0) return utterances;

        var renamed = utterances
            .Select(u => names.TryGetValue(u.Speaker, out var n) && !string.IsNullOrWhiteSpace(n) ? u with { Speaker = n.Trim() } : u)
            .ToList();
        // Two clusters mapped to the same person should read as one continuous turn.
        return TranscriptBuilder.JoinAdjacent(renamed);
    }

    private async Task<SavedNote> Save(string fileName, string markdown, List<string> warnings, CancellationToken ct)
    {
        try
        {
            var destination = NoteDestinations.Create(settings.Output);
            try
            {
                return await destination.SaveAsync(fileName, markdown, ct);
            }
            finally
            {
                (destination as IDisposable)?.Dispose();
            }
        }
        catch (Exception e) when (settings.Output.Destination != OutputKind.MarkdownFolder && e is not OperationCanceledException)
        {
            // Never lose a transcript because Obsidian was closed.
            var saved = await new FolderDestination(AppPaths.Unsent).SaveAsync(fileName, markdown, ct);
            warnings.Add($"{e.Message} The note was saved to {saved.LocalPath} instead.");
            return saved;
        }
    }

    internal static List<string> ParseTags(string tags) =>
        tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.TrimStart('#').Replace(' ', '-'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Removes a session's folder, but only inside our own recordings folder.</summary>
    public static void DeleteWorkDirectory(string directory)
    {
        try
        {
            var full = Path.GetFullPath(directory);
            var root = Path.GetFullPath(AppPaths.Recordings) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
                Directory.Delete(full, recursive: true);
        }
        catch (IOException)
        {
            // A locked file just means the folder lingers; the transcript is already saved.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Wraps an existing audio file as a one-source recording so it can go through the same pipeline.</summary>
    public static SessionRecording FromFile(string path, string label, bool diarize)
    {
        var directory = Path.Combine(AppPaths.Recordings, "import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);
        var duration = AudioConvert.GetDuration(path);

        var written = new DateTimeOffset(File.GetLastWriteTime(path));
        return new SessionRecording(
            Path.GetFileNameWithoutExtension(path),
            written - duration,
            duration,
            directory,
            [new RecordedSource(path, label, SourceKind.File, diarize, Path.GetFileName(path))]);
    }

    private sealed class StepClock
    {
        private readonly List<string> _where = [];
        private TimeSpan _diarize;

        public TimeSpan Transcribe { get; set; }

        public void Diarized(TimeSpan elapsed, string where)
        {
            lock (_where)
            {
                _diarize += elapsed;
                if (!_where.Contains(where)) _where.Add(where);
            }
        }

        public StepTimings ToTimings() =>
            new(Transcribe.TotalSeconds, _diarize.TotalSeconds, _where.Count == 0 ? null : string.Join(" and ", _where));
    }
}
