using System.Text;
using System.Text.RegularExpressions;
using Transcriber.Core.Audio;
using Transcriber.Core.Diarization;

namespace Transcriber.Core.Transcript;

public static partial class TranscriptBuilder
{
    /// <summary>A pause this long starts a new paragraph even when the speaker doesn't change.</summary>
    internal const double ParagraphGapSeconds = 3.0;

    /// <summary>Interleaves every source's utterances by start time.</summary>
    public static List<Utterance> Merge(IEnumerable<IEnumerable<Utterance>> perSource, bool suppressEcho)
    {
        var all = perSource.SelectMany(u => u).OrderBy(u => u.Start).ToList();
        return suppressEcho ? SuppressEcho(all) : all;
    }

    /// <summary>Joins back-to-back utterances by the same speaker that are separated by only a short pause.</summary>
    public static List<Utterance> JoinAdjacent(IReadOnlyList<Utterance> utterances)
    {
        var result = new List<Utterance>(utterances.Count);
        foreach (var u in utterances)
        {
            if (result.Count > 0 && result[^1] is var last && last.Speaker == u.Speaker
                && u.Start - last.End < ParagraphGapSeconds)
            {
                result[^1] = last with { End = Math.Max(last.End, u.End), Text = last.Text + " " + u.Text };
            }
            else
            {
                result.Add(u);
            }
        }
        return result;
    }

    /// <summary>Turns one source's words into speaker-attributed utterances.</summary>
    public static List<Utterance> BuildForSource(SourceTranscript source)
    {
        var speakers = AssignSpeakers(source);
        var result = new List<Utterance>();
        var text = new StringBuilder();
        string? current = null;
        double start = 0, end = 0;

        for (int i = 0; i < source.Words.Count; i++)
        {
            var w = source.Words[i];
            var speaker = speakers[i];
            bool newParagraph = current is null || speaker != current || w.Start - end >= ParagraphGapSeconds;
            if (newParagraph)
            {
                Flush();
                current = speaker;
                start = w.Start;
            }
            text.Append(w.Text);
            end = w.End;
        }
        Flush();
        return result;

        void Flush()
        {
            var t = NormalizeSpace(text.ToString());
            if (current is not null && t.Length > 0)
                result.Add(new Utterance(current, source.Kind, start, end, t));
            text.Clear();
        }
    }

    /// <summary>
    /// Labels each word with a speaker. Undiarized sources use the source label; diarized ones get
    /// "Label 1", "Label 2", ... numbered by first appearance, or just "Label" if only one voice was found.
    /// </summary>
    internal static string[] AssignSpeakers(SourceTranscript source)
    {
        var labels = new string[source.Words.Count];
        var turns = source.Turns;
        if (turns is null || turns.Count == 0)
        {
            Array.Fill(labels, source.Label);
            return labels;
        }

        var raw = new int[source.Words.Count];
        int previous = -1;
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = SpeakerFor(source.Words[i], turns) ?? previous;
            previous = raw[i];
        }
        // Words before the first matched turn inherit the first speaker that does match.
        int first = raw.FirstOrDefault(r => r >= 0, -1);
        for (int i = 0; i < raw.Length && raw[i] < 0; i++) raw[i] = first;

        var order = new Dictionary<int, int>();
        foreach (var r in raw)
            if (r >= 0 && !order.ContainsKey(r)) order[r] = order.Count + 1;

        for (int i = 0; i < raw.Length; i++)
            labels[i] = order.Count <= 1 || raw[i] < 0 ? source.Label : $"{source.Label} {order[raw[i]]}";
        return labels;
    }

    /// <summary>The turn overlapping the word most, else the nearest turn within a second.</summary>
    private static int? SpeakerFor(TimedWord word, IReadOnlyList<SpeakerTurn> turns)
    {
        double bestOverlap = 0;
        int? best = null;
        double nearestDistance = double.MaxValue;
        int? nearest = null;

        foreach (var t in turns)
        {
            double overlap = Math.Min(word.End, t.End) - Math.Max(word.Start, t.Start);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = t.Speaker;
            }
            double distance = overlap > 0 ? 0 : Math.Min(Math.Abs(word.Start - t.End), Math.Abs(t.Start - word.End));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = t.Speaker;
            }
        }
        return best ?? (nearestDistance <= 1.0 ? nearest : null);
    }

    /// <summary>
    /// When someone listens on speakers rather than headphones, the mic re-records the call. Drop mic
    /// utterances that overlap system-audio speech in time and mostly repeat its words.
    /// </summary>
    internal static List<Utterance> SuppressEcho(List<Utterance> utterances)
    {
        var system = utterances.Where(u => u.Kind == SourceKind.SystemAudio).ToList();
        if (system.Count == 0) return utterances;

        return utterances.Where(u => u.Kind != SourceKind.Microphone || !IsEcho(u, system)).ToList();
    }

    private static bool IsEcho(Utterance mic, List<Utterance> system)
    {
        var micWords = Tokens(mic.Text);
        if (micWords.Count == 0) return false;

        // Pool words from every system utterance the mic line overlaps (padded for device latency).
        var pool = new HashSet<string>();
        foreach (var s in system)
        {
            if (s.End < mic.Start - 1.0 || s.Start > mic.End + 1.0) continue;
            pool.UnionWith(Tokens(s.Text));
        }
        if (pool.Count == 0) return false;

        double shared = micWords.Count(pool.Contains);
        return shared / micWords.Count >= 0.6;
    }

    private static HashSet<string> Tokens(string text) =>
        WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToHashSet();

    private static string NormalizeSpace(string s) => SpaceRegex().Replace(s, " ").Trim();

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();
}
