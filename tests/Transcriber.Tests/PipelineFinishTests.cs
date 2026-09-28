using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;
using Transcriber.Core.Transcript;
using Xunit;

namespace Transcriber.Tests;

public sealed class PipelineFinishTests : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "transcriber-finish-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_out)) Directory.Delete(_out, recursive: true);
    }

    private async Task<string> Finish(IReadOnlyDictionary<string, string>? names)
    {
        var settings = new AppSettings { Output = { Destination = OutputKind.MarkdownFolder, MarkdownFolder = _out } };
        var words = new List<TimedWord> { new(0, 0.5, " Hi,"), new(0.5, 1, " I'm"), new(1, 1.5, " Priya."), new(3, 3.5, " Hello.") };
        var draft = new TranscriptDraft(
            [new SourceTranscript("Remote", SourceKind.SystemAudio, words, [new SpeakerTurn(0, 2, 0), new SpeakerTurn(2.5, 4, 1)])],
            "en", "test-model", [], new StepTimings(1, 1, "on the server"));
        var recording = new SessionRecording("Standup", DateTimeOffset.Now, TimeSpan.FromSeconds(4), _out, []);

        var result = await new TranscriptionPipeline(settings).FinishAsync(recording, draft, names, new Progress<string>(), default);
        return File.ReadAllText(result.Note.LocalPath!);
    }

    [Fact]
    public async Task No_names_applies_the_suggested_ones()
    {
        var md = await Finish(null);
        Assert.Contains("Priya", md.Split("---")[2]);
        Assert.Contains("test-model", md);
    }

    [Fact]
    public async Task Empty_names_keep_the_automatic_labels()
    {
        var md = await Finish(new Dictionary<string, string>());
        Assert.Contains("Remote 1", md);
        Assert.Contains("Remote 2", md);
    }

    [Fact]
    public void Reprocessing_reuses_a_16k_copy_instead_of_making_another()
    {
        Assert.Equal(Path.Combine("d", "a-16k.wav"), TranscriptionPipeline.Wav16Path("d", Path.Combine("d", "a.wav")));
        Assert.Equal(Path.Combine("d", "a-16k.wav"), TranscriptionPipeline.Wav16Path("d", Path.Combine("d", "a-16k.wav")));
    }
}
