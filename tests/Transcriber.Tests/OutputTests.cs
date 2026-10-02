using Transcriber.Core.Audio;
using Transcriber.Core.Output;
using Transcriber.Core.Transcript;
using Xunit;

namespace Transcriber.Tests;

public class OutputTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 25, 14, 3, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Renders_frontmatter_and_timestamped_lines()
    {
        var note = new NoteData(
            "Weekly: sync",
            Start,
            TimeSpan.FromSeconds(3725),
            [new NoteSource("Me", "Headset Mic"), new NoteSource("Remote", "Speakers")],
            [new Utterance("Me", SourceKind.Microphone, 3, 5, "Hello."), new Utterance("Priya", SourceKind.SystemAudio, 65, 70, "Hi!")],
            "large-v3-turbo",
            "en",
            ["transcript", "meeting"],
            LinkSpeakers: true);

        var md = MarkdownRenderer.Render(note);

        Assert.StartsWith("---\n", md.Replace("\r\n", "\n"));
        Assert.Contains("title: \"Weekly: sync\"", md);
        Assert.Contains("duration: \"01:02:05\"", md);
        Assert.Contains("  - \"[[Priya]]\"", md);
        Assert.Contains("  - \"Remote (Speakers)\"", md);
        Assert.Contains("start: \"14:03:00\"", md);
        Assert.Contains("end: \"15:05:05\"", md);
        Assert.Contains("**[14:03:03] [[Me]]:** Hello.", md);
        Assert.Contains("**[14:04:05] [[Priya]]:** Hi!", md);
    }

    [Fact]
    public void Renders_interjections_in_parentheses_and_lists_their_speakers()
    {
        var line = new Utterance("Priya", SourceKind.Microphone, 3, 6, "We need data for the intake.")
        {
            Interjections = [new("Tom", "We need data".Length, "yep"), new("Priya", "We need data for".Length, "um")],
        };
        var note = new NoteData("t", Start, TimeSpan.FromSeconds(6), [], [line], "m", null, [], LinkSpeakers: true);

        var md = MarkdownRenderer.Render(note);

        Assert.Contains("**[14:03:03] [[Priya]]:** We need data ([[Tom]]: yep) for um the intake.", md);
        Assert.Contains("  - \"[[Tom]]\"", md);
    }

    [Fact]
    public void Yaml_escapes_quotes_and_backslashes() =>
        Assert.Equal("\"say \\\"hi\\\" C:\\\\x\"", MarkdownRenderer.Yaml("say \"hi\" C:\\x"));

    [Theory]
    [InlineData("{date} {time} {title}", "Team: Q3/Q4 #plan", "2026-09-25 1403 Team Q3 Q4 plan.md")]
    [InlineData("{title}", "", "Transcript.md")]
    [InlineData("Meetings {date}", "x", "Meetings 2026-09-25.md")]
    public void Builds_safe_file_names(string template, string title, string expected) =>
        Assert.Equal(expected, FileNames.Build(template, title, Start));

    [Fact]
    public async Task Unique_name_appends_counter()
    {
        var taken = new HashSet<string> { "a.md", "a (2).md" };
        Assert.Equal("a (3).md", await FileNames.UniqueAsync("a.md", n => Task.FromResult(taken.Contains(n))));
    }

    [Fact]
    public async Task Folder_destination_never_overwrites()
    {
        var dir = Path.Combine(Path.GetTempPath(), "transcriber-test-" + Guid.NewGuid());
        try
        {
            var dest = new FolderDestination(dir);
            var first = await dest.SaveAsync("n.md", "one", default);
            var second = await dest.SaveAsync("n.md", "two", default);
            Assert.NotEqual(first.LocalPath, second.LocalPath);
            Assert.Equal("one", File.ReadAllText(first.LocalPath!));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class TimeOfDayTests
{
    [Fact]
    public void Uses_24_hour_clock_and_rolls_past_midnight()
    {
        var start = new DateTimeOffset(2026, 9, 25, 23, 59, 30, TimeSpan.Zero);
        Assert.Equal("23:59:45", Transcriber.Core.Output.MarkdownRenderer.TimeOfDay(start, 15.4));
        Assert.Equal("00:00:10", Transcriber.Core.Output.MarkdownRenderer.TimeOfDay(start, 40));
    }
}

public class BuildInfoTests
{
    [Theory]
    [InlineData("0.2.0+9a7c27bdeadbeef", "0.2.0", "9a7c27b")]
    [InlineData("1.4.2", "1.4.2", null)]
    [InlineData("0.1.0+", "0.1.0", null)]
    public void Parses_informational_version(string info, string version, string? commit)
    {
        var build = Transcriber.Core.BuildInfo.Parse(info);
        Assert.Equal(version, build.Version);
        Assert.Equal(commit, build.Commit);
    }

    [Fact]
    public void Note_records_the_app_that_made_it()
    {
        var note = new NoteData("t", DateTimeOffset.Now, TimeSpan.Zero, [], [], "m", null, [], false) { App = "Transcriber for Android 0.2.0" };
        Assert.Contains("app: \"Transcriber for Android 0.2.0\"", MarkdownRenderer.Render(note));
    }
}
