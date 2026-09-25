using Transcriber.Core.Audio;
using Transcriber.Core.Transcript;
using Xunit;

namespace Transcriber.Tests;

public class SpeakerNamesTests
{
    [Theory]
    [InlineData("Hi everyone, I'm Priya from the data team.", "Priya")]
    [InlineData("Hey, this is Tom.", "Tom")]
    [InlineData("My name is Alex and I run ops.", "Alex")]
    public void Finds_self_introductions(string text, string name) =>
        Assert.Equal([name], SpeakerNames.Introductions(text));

    [Theory]
    [InlineData("I'm sure that works.")]
    [InlineData("It's Monday again.")]
    [InlineData("This is Microsoft's new policy.")]
    public void Ignores_non_names(string text) =>
        Assert.Empty(SpeakerNames.Introductions(text));

    [Fact]
    public void Suggests_names_only_for_generic_speakers_and_never_twice()
    {
        var utterances = new List<Utterance>
        {
            new("Me", SourceKind.Microphone, 0, 2, "Hi, I'm Jordan."),
            new("Remote 1", SourceKind.SystemAudio, 3, 5, "Hi Jordan, I'm Priya."),
            new("Remote 2", SourceKind.SystemAudio, 6, 8, "And I'm Priya too, apparently. Well, I'm Sam."),
        };
        var generic = new HashSet<string> { "Remote 1", "Remote 2" };

        var summary = SpeakerNames.Summarize(utterances, generic).ToDictionary(s => s.Speaker);

        Assert.Null(summary["Me"].SuggestedName);
        Assert.False(summary["Me"].IsGeneric);
        Assert.Equal("Priya", summary["Remote 1"].SuggestedName);
        Assert.Equal("Sam", summary["Remote 2"].SuggestedName);
    }
}

public class AddresseeTests
{
    [Theory]
    [InlineData("Hi, Sanjay. I'm very much interested in learning about AI.", "Sanjay")]
    [InlineData("Thanks Tom, that helps.", "Tom")]
    [InlineData("Over to you, Priya.", "Priya")]
    [InlineData("Hi everyone.", null)]
    [InlineData("Thanks Monday for nothing.", null)]
    [InlineData("I said hi, Sam, earlier.", null)]
    public void Detects_name_after_greeting(string text, string? expected) =>
        Assert.Equal(expected, SpeakerNames.Addressee(text));

    [Fact]
    public void Addressed_name_goes_to_whoever_replies()
    {
        var utterances = new List<Utterance>
        {
            new("Speaker 1", SourceKind.File, 0, 10, "Hi, Sanjay. Can you explain generative AI?"),
            new("Speaker 2", SourceKind.File, 12, 20, "Yeah, sure."),
        };
        var summary = SpeakerNames.Summarize(utterances, new HashSet<string> { "Speaker 1", "Speaker 2" })
            .ToDictionary(s => s.Speaker);

        Assert.Equal("Sanjay", summary["Speaker 2"].SuggestedName);
        Assert.Null(summary["Speaker 1"].SuggestedName);
    }
}
