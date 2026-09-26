using Transcriber.Core.Output;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;
using Xunit;
using Xunit.Abstractions;

namespace Transcriber.Tests;

/// <summary>
/// End-to-end run against a real STT server. Set TRANSCRIBER_STT_URL and TRANSCRIBER_TEST_AUDIO
/// (a WAV with more than one speaker) to enable; otherwise the test passes without doing anything.
/// </summary>
public class ServerIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Transcribes_and_separates_speakers_into_markdown()
    {
        var url = Environment.GetEnvironmentVariable("TRANSCRIBER_STT_URL");
        var audio = Environment.GetEnvironmentVariable("TRANSCRIBER_TEST_AUDIO");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(audio)) return;

        var outDir = Path.Combine(Path.GetTempPath(), "transcriber-it-" + Guid.NewGuid());
        var settings = new AppSettings
        {
            Stt = { BaseUrl = url },
            Speakers = { ReviewNames = false },
            Output = { Destination = OutputKind.MarkdownFolder, MarkdownFolder = outDir },
        };
        var threshold = Environment.GetEnvironmentVariable("TRANSCRIBER_THRESHOLD");
        if (threshold is not null) settings.Speakers.ClusterThreshold = float.Parse(threshold);

        var recording = TranscriptionPipeline.FromFile(audio, "Speaker", diarize: true);
        var progress = new Progress<string>(m => output.WriteLine(m));
        var result = await new TranscriptionPipeline(settings).RunAsync(recording, progress, default);

        var md = File.ReadAllText(result.Note.LocalPath!);
        output.WriteLine(md);
        foreach (var w in result.Warnings) output.WriteLine("WARN: " + w);

        Assert.Contains("**[", md);
        // Names may be auto-suggested ("Sanjay") since review is off, so count voices rather than match labels.
        var voices = md.Split('\n')
            .SkipWhile(l => !l.StartsWith("speakers:"))
            .Skip(1)
            .TakeWhile(l => l.StartsWith("  - "))
            .Count();
        Assert.True(voices >= 2, $"expected at least two speakers, found {voices}");
    }
}
