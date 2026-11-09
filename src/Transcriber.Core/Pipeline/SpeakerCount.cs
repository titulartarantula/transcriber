using Transcriber.Core.Audio;

namespace Transcriber.Core.Pipeline;

/// <summary>Turns "how many people are on the call" into the speaker count each diarized source is given.</summary>
public static class SpeakerCount
{
    /// <summary>
    /// The voice count to send for each diarized source, given the total number of people on the call.
    /// Every recorded source that isn't diarized is one known person (the "Me" microphone), so they're taken
    /// off the total, leaving at least 1. 0 means let the server estimate.
    /// </summary>
    /// <param name="people">Everyone on the call, including the user, or 0 if unknown.</param>
    /// <param name="sources">The recorded sources, with the diarization they'll actually get.</param>
    /// <remarks>
    /// With more than one diarized source the total can't be split between them: nothing says how many of
    /// the remaining people are on each. The server's num_speakers is an exact count, not a maximum, so
    /// sending the total to each source would make it invent voices, which is the bug this exists to fix.
    /// Those recordings send nothing and the server estimates per source. With no diarized source the count
    /// isn't used at all, so it's 0 too.
    /// </remarks>
    public static int PerDiarizedSource(int people, IReadOnlyCollection<RecordedSource> sources)
    {
        if (people <= 0) return 0;
        int diarized = sources.Count(s => s.Diarize);
        if (diarized != 1) return 0;
        int known = sources.Count - diarized;
        return Math.Max(1, people - known);
    }
}
