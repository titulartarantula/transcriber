using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;

namespace Transcriber.Core.Transcript;

/// <param name="Segment">
/// Index of the Whisper segment (roughly a sentence) the word came from, or -1 if unknown, as in drafts
/// saved before it was recorded.
/// </param>
public sealed record TimedWord(double Start, double End, string Text, int Segment = -1);

/// <summary>Everything known about one recorded source after STT and (optional) diarization.</summary>
public sealed record SourceTranscript(
    string Label,
    SourceKind Kind,
    IReadOnlyList<TimedWord> Words,
    IReadOnlyList<SpeakerTurn>? Turns);

/// <summary>A run of words from one speaker.</summary>
public sealed record Utterance(string Speaker, SourceKind Kind, double Start, double End, string Text)
{
    public double Duration => End - Start;

    /// <summary>Remarks by others ("yep", "oh yeah") made while this speaker held the floor, in order.</summary>
    public IReadOnlyList<Interjection> Interjections { get; init; } = [];
}

/// <summary>A short remark by someone else inside an utterance, shown in parentheses at <see cref="Offset"/> in its text.</summary>
public sealed record Interjection(string Speaker, int Offset, string Text);

public sealed record SpeakerSummary(string Speaker, bool IsGeneric, TimeSpan TalkTime, string Sample, string? SuggestedName);
