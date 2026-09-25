using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;

namespace Transcriber.Core.Transcript;

public sealed record TimedWord(double Start, double End, string Text);

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
}

public sealed record SpeakerSummary(string Speaker, bool IsGeneric, TimeSpan TalkTime, string Sample, string? SuggestedName);
