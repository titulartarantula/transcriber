using Transcriber.Core.Audio;
using Transcriber.Core.Pipeline;
using Xunit;

namespace Transcriber.Tests;

public class SpeakerCountTests
{
    private static readonly RecordedSource Mic = new("mic.wav", "Me", SourceKind.Microphone, false, "Headset");
    private static readonly RecordedSource Remote = new("out.wav", "Remote", SourceKind.SystemAudio, true, "Speakers");

    [Fact]
    public void The_unsplit_mic_is_one_of_the_people_on_the_call() =>
        Assert.Equal(3, SpeakerCount.PerDiarizedSource(4, [Mic, Remote]));

    [Fact]
    public void Never_asks_for_fewer_than_one_voice() =>
        Assert.Equal(1, SpeakerCount.PerDiarizedSource(1, [Mic, Remote]));

    [Fact]
    public void No_count_sends_nothing() =>
        Assert.Equal(0, SpeakerCount.PerDiarizedSource(0, [Mic, Remote]));

    [Fact]
    public void A_single_split_source_gets_the_whole_count() =>
        Assert.Equal(4, SpeakerCount.PerDiarizedSource(4, [Remote]));

    [Fact]
    public void Several_split_sources_are_left_to_estimate() =>
        Assert.Equal(0, SpeakerCount.PerDiarizedSource(5, [Mic with { Diarize = true }, Remote]));

    [Fact]
    public void Nothing_split_needs_no_count() =>
        Assert.Equal(0, SpeakerCount.PerDiarizedSource(3, [Mic, Remote with { Diarize = false }]));
}
