using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;
using Transcriber.Core.Transcript;
using Xunit;

namespace Transcriber.Tests;

public class TranscriptBuilderTests
{
    private static List<TimedWord> Words(params (double Start, double End, string Text)[] w) =>
        w.Select(x => new TimedWord(x.Start, x.End, " " + x.Text)).ToList();

    [Fact]
    public void Undiarized_source_uses_its_label()
    {
        var src = new SourceTranscript("Me", SourceKind.Microphone, Words((0, 0.5, "Hello"), (0.5, 1, "there")), null);
        var u = Assert.Single(TranscriptBuilder.BuildForSource(src));
        Assert.Equal("Me", u.Speaker);
        Assert.Equal("Hello there", u.Text);
    }

    [Fact]
    public void Diarized_source_numbers_speakers_by_first_appearance()
    {
        var words = Words((0, 0.5, "Hi"), (0.6, 1.0, "all"), (2.0, 2.4, "Hey"), (2.5, 3.0, "back"), (4.0, 4.5, "Great"));
        // Raw cluster ids are arbitrary; 7 speaks first so becomes "Remote 1".
        var turns = new List<SpeakerTurn> { new(0, 1.1, 7), new(1.9, 3.1, 2), new(3.9, 5, 7) };
        var result = TranscriptBuilder.BuildForSource(new SourceTranscript("Remote", SourceKind.SystemAudio, words, turns));

        Assert.Equal(["Remote 1", "Remote 2", "Remote 1"], result.Select(u => u.Speaker));
        Assert.Equal("Hey back", result[1].Text);
    }

    [Fact]
    public void Single_diarized_voice_keeps_plain_label()
    {
        var words = Words((0, 0.5, "Only"), (0.6, 1.0, "me"));
        var turns = new List<SpeakerTurn> { new(0, 1.1, 3) };
        var u = Assert.Single(TranscriptBuilder.BuildForSource(new SourceTranscript("Remote", SourceKind.SystemAudio, words, turns)));
        Assert.Equal("Remote", u.Speaker);
    }

    [Fact]
    public void Word_in_gap_between_turns_goes_to_nearest_turn()
    {
        var words = Words((0, 0.5, "a"), (1.25, 1.4, "b"), (2.0, 2.5, "c"));
        var turns = new List<SpeakerTurn> { new(0, 0.6, 0), new(1.5, 3, 1) };
        var labels = TranscriptBuilder.AssignSpeakers(new SourceTranscript("R", SourceKind.SystemAudio, words, turns));
        Assert.Equal(["R 1", "R 2", "R 2"], labels);
    }

    [Fact]
    public void Long_pause_splits_paragraph_for_same_speaker()
    {
        var src = new SourceTranscript("Me", SourceKind.Microphone, Words((0, 1, "one"), (5, 6, "two")), null);
        Assert.Equal(2, TranscriptBuilder.BuildForSource(src).Count);
    }

    [Fact]
    public void Sources_interleave_by_time()
    {
        var me = new List<Utterance> { new("Me", SourceKind.Microphone, 0, 1, "a"), new("Me", SourceKind.Microphone, 4, 5, "c") };
        var them = new List<Utterance> { new("Remote", SourceKind.SystemAudio, 2, 3, "b") };
        var merged = TranscriptBuilder.Merge([me, them], suppressEcho: false);
        Assert.Equal(["a", "b", "c"], merged.Select(u => u.Text));
    }

    [Fact]
    public void Echo_of_system_audio_on_mic_is_dropped()
    {
        var system = new Utterance("Remote", SourceKind.SystemAudio, 10, 14, "Can everyone see my screen right now?");
        var echo = new Utterance("Me", SourceKind.Microphone, 10.2, 14.1, "can everyone see my screen right now");
        var real = new Utterance("Me", SourceKind.Microphone, 15, 16, "Yes, I can see it.");
        var merged = TranscriptBuilder.Merge([[echo, real], [system]], suppressEcho: true);
        Assert.Equal(["Can everyone see my screen right now?", "Yes, I can see it."], merged.Select(u => u.Text));
    }

    [Fact]
    public void Crosstalk_that_overlaps_but_differs_is_kept()
    {
        var system = new Utterance("Remote", SourceKind.SystemAudio, 10, 14, "So the budget is due Friday");
        var mic = new Utterance("Me", SourceKind.Microphone, 12, 13, "Sorry, which budget?");
        Assert.Equal(2, TranscriptBuilder.Merge([[mic], [system]], suppressEcho: true).Count);
    }

    [Fact]
    public void JoinAdjacent_merges_renamed_clusters()
    {
        var list = new List<Utterance>
        {
            new("Priya", SourceKind.SystemAudio, 0, 2, "First part."),
            new("Priya", SourceKind.SystemAudio, 2.5, 4, "Second part."),
            new("Me", SourceKind.Microphone, 5, 6, "Reply."),
        };
        var joined = TranscriptBuilder.JoinAdjacent(list);
        Assert.Equal(2, joined.Count);
        Assert.Equal("First part. Second part.", joined[0].Text);
    }
}
