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
    public void Compact_copy_is_the_one_the_pipeline_made_else_a_compressed_source()
    {
        Directory.CreateDirectory(_out);
        string File(string name)
        {
            var path = Path.Combine(_out, name);
            System.IO.File.WriteAllText(path, "x");
            return path;
        }

        var raw = File("source1-SystemAudio.wav");
        Assert.Null(TranscriptionPipeline.CompactCopy(_out, raw));
        var made = File("source1-SystemAudio-compact.m4a");
        Assert.Equal(made, TranscriptionPipeline.CompactCopy(_out, raw));
        Assert.Equal(made, TranscriptionPipeline.CompactCopy(_out, made));

        var phone = File("mic.aac");
        Assert.Equal(phone, TranscriptionPipeline.CompactCopy(_out, phone));
        var wav16 = File("mic2-16k.wav");
        Assert.Equal(wav16, TranscriptionPipeline.CompactCopy(_out, wav16));
    }

    [Fact]
    public void Only_small_compressed_files_count_as_compact()
    {
        Directory.CreateDirectory(_out);
        var aac = Path.Combine(_out, "a.aac");
        System.IO.File.WriteAllBytes(aac, new byte[6000]); // 48 kbps for a second
        var wav = Path.Combine(_out, "a.wav");
        System.IO.File.WriteAllBytes(wav, new byte[6000]);
        var big = Path.Combine(_out, "b.m4a");
        System.IO.File.WriteAllBytes(big, new byte[64000]); // 512 kbps

        Assert.True(AudioConvert.IsCompact(aac, TimeSpan.FromSeconds(1)));
        Assert.False(AudioConvert.IsCompact(wav, TimeSpan.FromSeconds(1)));
        Assert.False(AudioConvert.IsCompact(big, TimeSpan.FromSeconds(1)));
    }
}
