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

    // Words with the Whisper segment each came from.
    private static List<TimedWord> Words(params (double Start, double End, string Text, int Segment)[] w) =>
        w.Select(x => new TimedWord(x.Start, x.End, " " + x.Text, x.Segment)).ToList();

    [Fact]
    public void Sliver_of_another_speaker_mid_sentence_goes_back_to_the_talker()
    {
        // Overlapping speech: diarization gave "of" to whoever said "mm" over it.
        var words = Words((0, 0.3, "an", 0), (0.3, 0.8, "intake", 0), (0.8, 1.0, "of", 0), (1.0, 1.3, "some", 0), (1.3, 1.6, "kind", 0));
        var turns = new List<SpeakerTurn> { new(0, 0.8, 0), new(0.8, 1.0, 1), new(1.0, 2, 0) };
        var u = Assert.Single(TranscriptBuilder.BuildForSource(new SourceTranscript("R", SourceKind.Microphone, words, turns)));
        Assert.Equal("an intake of some kind", u.Text);
        Assert.Empty(u.Interjections);
    }

    [Fact]
    public void Short_reply_in_its_own_sentence_keeps_its_speaker()
    {
        var words = Words((0, 0.5, "really", 0), (0.5, 1.0, "confused", 0), (1.0, 1.6, "probably", 1), (1.6, 2.0, "is", 1), (2.0, 2.5, "anyway", 2));
        var turns = new List<SpeakerTurn> { new(0, 1.0, 0), new(1.0, 2.0, 1), new(2.0, 3, 0) };
        var result = TranscriptBuilder.BuildForSource(new SourceTranscript("R", SourceKind.Microphone, words, turns));
        Assert.Equal(["R 1", "R 2", "R 1"], result.Select(u => u.Speaker));
    }

    [Fact]
    public void Slivers_in_a_flurry_resolve_to_whoever_holds_most_of_it()
    {
        // A B A B A within one sentence, A holding the longer pieces.
        var words = Words((0, 1.0, "trying", 0), (1.0, 1.2, "to", 0), (1.2, 2.0, "be", 0), (2.0, 2.3, "really", 0), (2.3, 3.5, "clear", 0));
        var turns = new List<SpeakerTurn> { new(0, 1.0, 0), new(1.0, 1.2, 1), new(1.2, 2.0, 0), new(2.0, 2.3, 1), new(2.3, 4, 0) };
        var u = Assert.Single(TranscriptBuilder.BuildForSource(new SourceTranscript("R", SourceKind.Microphone, words, turns)));
        Assert.Equal("trying to be really clear", u.Text);
    }

    [Fact]
    public void Remark_while_someone_talks_becomes_an_interjection()
    {
        var words = Words((0, 0.4, "we", 0), (0.4, 0.7, "need", 0), (0.7, 1.0, "data", 0), (1.0, 1.3, "yep", 0), (1.3, 1.6, "for", 0), (1.6, 2.0, "intake", 0));
        var turns = new List<SpeakerTurn> { new(0, 1.0, 0), new(1.0, 1.3, 1), new(1.3, 2.5, 0) };
        var u = Assert.Single(TranscriptBuilder.BuildForSource(new SourceTranscript("R", SourceKind.Microphone, words, turns)));
        Assert.Equal("R 1", u.Speaker);
        Assert.Equal("we need data for intake", u.Text);
        Assert.Equal(new Interjection("R 2", "we need data".Length, "yep"), Assert.Single(u.Interjections));
    }

    [Fact]
    public void Remark_among_slivers_doesnt_pull_them_its_way()
    {
        // From a real recording: "academic | yep | data | there's | an intake | of | some kind".
        var words = Words((0, 0.6, "academic", 0), (0.6, 0.8, "yep", 0), (0.8, 1.1, "data", 0), (1.1, 1.4, "there's", 0),
            (1.4, 2.4, "an", 0), (2.4, 2.8, "intake", 0), (2.8, 3.0, "of", 0), (3.0, 3.6, "some", 0), (3.6, 4.0, "kind", 0));
        var turns = new List<SpeakerTurn>
        {
            new(0, 0.6, 0), new(0.6, 0.8, 1), new(0.8, 1.1, 0), new(1.1, 1.4, 1), new(1.4, 2.8, 0), new(2.8, 3.0, 1), new(3.0, 5, 0),
        };
        var u = Assert.Single(TranscriptBuilder.BuildForSource(new SourceTranscript("R", SourceKind.Microphone, words, turns)));
        Assert.Equal("academic data there's an intake of some kind", u.Text);
        Assert.Equal(new Interjection("R 2", "academic".Length, "yep"), Assert.Single(u.Interjections));
    }

    [Fact]
    public void Remark_that_ends_the_exchange_is_its_own_turn()
    {
        var words = Words((0, 0.5, "makes", 0), (0.5, 1.0, "sense", 0), (1.5, 2.0, "yeah", 1));
        var turns = new List<SpeakerTurn> { new(0, 1.0, 0), new(1.5, 2.0, 1) };
        var result = TranscriptBuilder.BuildForSource(new SourceTranscript("R", SourceKind.Microphone, words, turns));
        Assert.Equal(["R 1", "R 2"], result.Select(u => u.Speaker));
    }

    [Fact]
    public void Talker_keeps_words_said_while_someone_murmurs_underneath()
    {
        // Exclusive turns gave "get what" to whoever murmured; the overlapping ones show the talker never stopped.
        var words = Words((0, 1, "if"), (1, 2, "people"), (2.0, 2.3, "get"), (2.3, 2.6, "what"), (2.6, 3.5, "matters"));
        var exclusive = new List<SpeakerTurn> { new(0, 2.0, 0), new(2.0, 2.6, 1), new(2.6, 6, 0) };
        var src = new SourceTranscript("R", SourceKind.Microphone, words, exclusive)
        {
            OverlappingTurns = [new(0, 6, 0), new(1.9, 2.7, 1)],
        };
        Assert.Equal("if people get what matters", Assert.Single(TranscriptBuilder.BuildForSource(src)).Text);
    }

    [Fact]
    public void Overlap_where_one_person_takes_over_from_another_is_left_alone()
    {
        var words = Words((0, 1, "so"), (1, 2.6, "anyway"), (2.6, 3.2, "right"), (3.2, 5, "exactly"));
        var exclusive = new List<SpeakerTurn> { new(0, 2.6, 0), new(2.6, 6, 1) };
        var src = new SourceTranscript("R", SourceKind.Microphone, words, exclusive)
        {
            OverlappingTurns = [new(0, 3.0, 0), new(2.5, 6, 1)],
        };
        Assert.Equal(["R 1", "R 1", "R 2", "R 2"], TranscriptBuilder.AssignSpeakers(src));
    }

    [Fact]
    public void Yeah_said_under_a_long_turn_stays_with_the_listener()
    {
        var words = Words((0, 1, "we"), (1, 2, "need"), (2.0, 2.3, "yeah"), (2.3, 3, "data"));
        var exclusive = new List<SpeakerTurn> { new(0, 2.0, 0), new(2.0, 2.3, 1), new(2.3, 6, 0) };
        var src = new SourceTranscript("R", SourceKind.Microphone, words, exclusive)
        {
            OverlappingTurns = [new(0, 6, 0), new(2.0, 2.3, 1)],
        };
        var u = Assert.Single(TranscriptBuilder.BuildForSource(src));
        Assert.Equal(new Interjection("R 2", "we need".Length, "yeah"), Assert.Single(u.Interjections));
    }

    [Theory]
    [InlineData("yep", true)]
    [InlineData(" Oh my God.", true)]
    [InlineData("yeah, okay", true)]
    [InlineData("mm-hmm", true)]
    [InlineData("of", false)]
    [InlineData("yes I agree", false)]
    public void Recognises_interjections(string text, bool expected) =>
        Assert.Equal(expected, TranscriptBuilder.IsInterjection(text));

    [Fact]
    public void JoinAdjacent_keeps_interjections_in_place()
    {
        var list = new List<Utterance>
        {
            new("Priya", SourceKind.SystemAudio, 0, 2, "First part."),
            new("Priya", SourceKind.SystemAudio, 2.5, 4, "Second part.") { Interjections = [new("Tom", 6, "yep")] },
        };
        var joined = Assert.Single(TranscriptBuilder.JoinAdjacent(list));
        Assert.Equal("First part. Second part.", joined.Text);
        Assert.Equal("First part. Second".Length, Assert.Single(joined.Interjections).Offset);
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
