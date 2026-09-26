using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;
using Transcriber.Core.Output;
using Transcriber.Core.Settings;
using Transcriber.Core.Stt;
using Transcriber.Core.Transcript;

namespace Transcriber.Core.Pipeline;

public sealed record PipelineResult(SavedNote Note, IReadOnlyList<string> Warnings, int UtteranceCount);

/// <summary>Speaker label → chosen display name. Return null to keep the automatic labels.</summary>
public delegate Task<IReadOnlyDictionary<string, string>?> SpeakerReview(
    IReadOnlyList<SpeakerSummary> speakers, CancellationToken ct);

/// <summary>Recording → per-source STT and diarization → merged, speaker-labelled note → destination.</summary>
public sealed class TranscriptionPipeline(AppSettings settings, SpeakerReview? review = null)
{
    public async Task<PipelineResult> RunAsync(SessionRecording recording, IProgress<string> progress, CancellationToken ct)
    {
        var warnings = new List<string>();

        bool diarize = settings.Speakers.Diarize && recording.Sources.Any(s => s.Diarize);
        if (diarize)
        {
            try
            {
                await DiarizationModels.EnsureAsync(progress, ct);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                warnings.Add($"Speaker separation was skipped because its models could not be downloaded ({e.Message}).");
                diarize = false;
            }
        }

        using var stt = new WhisperClient(settings.Stt);
        var perSource = new List<List<Utterance>>();
        var generic = new HashSet<string>();
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

            var wav16 = Path.Combine(recording.Directory, Path.GetFileNameWithoutExtension(source.FilePath) + "-16k.wav");
            await Task.Run(() => AudioConvert.WriteWav16(wav16, samples), ct);

            bool separate = diarize && source.Diarize;
            progress.Report(separate
                ? $"Transcribing {source.Label} and separating speakers…"
                : $"Transcribing {source.Label}…");

            var (result, turns) = await TranscribeAndDiarize(stt, wav16, samples, separate, source.Label, warnings, ct);
            language ??= result.Language;

            var transcript = new SourceTranscript(source.Label, source.Kind, WordExtractor.Extract(result), turns);
            var utterances = TranscriptBuilder.BuildForSource(transcript);
            if (turns is not null) generic.UnionWith(utterances.Select(u => u.Speaker));
            perSource.Add(utterances);
        }

        var merged = TranscriptBuilder.Merge(perSource, settings.Speakers.SuppressEcho);
        merged = await ApplySpeakerNames(merged, generic, ct);

        var title = string.IsNullOrWhiteSpace(recording.Title) ? "Transcript" : recording.Title.Trim();
        var note = new NoteData(
            title,
            recording.StartedAt,
            recording.Duration,
            recording.Sources.Select(s => new NoteSource(s.Label, s.DeviceName)).ToList(),
            merged,
            settings.Stt.Model,
            language,
            ParseTags(settings.Output.Tags),
            settings.Output.LinkSpeakers)
        {
            App = ProductInfo.Client,
        };

        var markdown = MarkdownRenderer.Render(note);
        var fileName = FileNames.Build(settings.Output.FileNameTemplate, title, recording.StartedAt);

        progress.Report("Saving note…");
        var saved = await Save(fileName, markdown, warnings, ct);

        TryDeleteWorkDirectory(recording.Directory);
        return new PipelineResult(saved, warnings, merged.Count);
    }

    private async Task<(WhisperResult, IReadOnlyList<SpeakerTurn>?)> TranscribeAndDiarize(
        WhisperClient stt, string wav16, float[] samples, bool separate, string label, List<string> warnings, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var speakers = settings.Speakers;

        // Diarization is CPU-bound and local while STT waits on the server, so run them side by side.
        var diarization = separate
            ? Task.Run(() => Diarizer.Run(samples, speakers.ExpectedSpeakers, speakers.ClusterThreshold, ct: linked.Token), linked.Token)
            : Task.FromResult<IReadOnlyList<SpeakerTurn>>([]);

        WhisperResult result;
        try
        {
            result = await stt.TranscribeAsync(wav16, ct);
        }
        catch
        {
            linked.Cancel();
            try { await diarization; } catch { /* the STT failure is the one worth reporting */ }
            throw;
        }

        if (!separate) return (result, null);
        try
        {
            return (result, await diarization);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            warnings.Add($"Speaker separation failed for {label}; its lines are labelled \"{label}\" ({e.Message}).");
            return (result, null);
        }
    }

    private async Task<List<Utterance>> ApplySpeakerNames(List<Utterance> utterances, HashSet<string> generic, CancellationToken ct)
    {
        var summaries = SpeakerNames.Summarize(utterances, generic);
        if (summaries.Count == 0) return utterances;

        IReadOnlyDictionary<string, string>? names;
        if (settings.Speakers.ReviewNames && review is not null)
            names = await review(summaries, ct);
        else
            names = summaries.Where(s => s.SuggestedName is not null).ToDictionary(s => s.Speaker, s => s.SuggestedName!);

        if (names is null || names.Count == 0) return utterances;

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

    /// <summary>Removes the session's scratch WAVs, but only inside our own recordings folder.</summary>
    private static void TryDeleteWorkDirectory(string directory)
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
}
