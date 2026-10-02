using Transcriber.Core.Stt;

namespace Transcriber.Core.Transcript;

public static class WordExtractor
{
    /// <summary>
    /// Pulls timed words out of a Whisper response, dropping segments Whisper itself flags as
    /// probable silence or repetition loops.
    /// </summary>
    public static IReadOnlyList<TimedWord> Extract(WhisperResult result, double offsetSeconds = 0)
    {
        var words = new List<TimedWord>();
        int index = -1;
        foreach (var seg in result.Segments)
        {
            index++;
            // Same thresholds Whisper uses to decide a window was silence or a hallucination loop.
            if (seg.NoSpeechProb > 0.6 && seg.AvgLogprob < -1.0) continue;
            if (seg.CompressionRatio > 2.4) continue;

            var segWords = seg.Words is { Count: > 0 }
                ? seg.Words
                : result.Words?.Where(w => w.Start >= seg.Start - 0.05 && w.End <= seg.End + 0.05).ToList();

            if (segWords is { Count: > 0 })
            {
                foreach (var w in segWords)
                {
                    if (string.IsNullOrWhiteSpace(w.Word)) continue;
                    words.Add(new TimedWord(w.Start + offsetSeconds, w.End + offsetSeconds, w.Word, index));
                }
            }
            else if (!string.IsNullOrWhiteSpace(seg.Text))
            {
                // Server without word timestamps: keep the segment as one coarse "word".
                words.Add(new TimedWord(seg.Start + offsetSeconds, seg.End + offsetSeconds, seg.Text, index));
            }
        }
        return words;
    }
}
