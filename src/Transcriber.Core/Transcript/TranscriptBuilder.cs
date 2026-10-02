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
                int shift = last.Text.Length + 1;
                result[^1] = last with
                {
                    End = Math.Max(last.End, u.End),
                    Text = last.Text + " " + u.Text,
                    Interjections = [.. last.Interjections, .. u.Interjections.Select(x => x with { Offset = x.Offset + shift })],
                };
            }
            else
            {
                result.Add(u);
            }
        }
        return result;
    }

    /// <summary>Longest remark, in words and seconds, that's shown as an interjection rather than its own turn.</summary>
    internal const int MaxInterjectionWords = 4;

    /// <inheritdoc cref="MaxInterjectionWords"/>
    internal const double MaxInterjectionSeconds = 2.0;

    /// <summary>
    /// Turns one source's words into speaker-attributed utterances. A short remark like "yep" from someone
    /// else in the middle of a speaker's turn goes into that turn as an <see cref="Interjection"/> rather
    /// than splitting it in two.
    /// </summary>
    public static List<Utterance> BuildForSource(SourceTranscript source)
    {
        var words = source.Words;
        var speakers = AssignSpeakers(source);
        var result = new List<Utterance>();
        var text = new StringBuilder();
        var asides = new List<(string Speaker, int At, string Text)>();
        string? current = null;
        double start = 0, end = 0;

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var speaker = speakers[i];
            if (current is not null && speaker != current && InterjectionEnd(i) is int last)
            {
                asides.Add((speaker, text.Length, Join(words, i, last)));
                i = last;
                continue;
            }

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

        // Where the run of another speaker starting at i ends, if it's a short remark followed by the
        // current speaker carrying on.
        int? InterjectionEnd(int i)
        {
            int last = i;
            while (last + 1 < words.Count && speakers[last + 1] == speakers[i]) last++;
            bool resumes = last + 1 < words.Count && speakers[last + 1] == current;
            bool brief = last - i + 1 <= MaxInterjectionWords && words[last].End - words[i].Start <= MaxInterjectionSeconds;
            return resumes && brief && IsInterjection(Join(words, i, last)) ? last : null;
        }

        void Flush()
        {
            var raw = text.ToString();
            var t = NormalizeSpace(raw);
            if (current is not null && t.Length > 0)
            {
                result.Add(new Utterance(current, source.Kind, start, end, t)
                {
                    Interjections = asides.Select(a => new Interjection(a.Speaker, NormalizeSpace(raw[..a.At]).Length, a.Text)).ToList(),
                });
            }
            text.Clear();
            asides.Clear();
        }
    }

    private static string Join(IReadOnlyList<TimedWord> words, int from, int to) =>
        NormalizeSpace(string.Concat(words.Skip(from).Take(to - from + 1).Select(w => w.Text)));

    /// <summary>
    /// Whether the words are only what people say to show they're listening ("yeah", "mm-hmm", "oh my god",
    /// "that's right"), as opposed to taking the floor.
    /// </summary>
    internal static bool IsInterjection(string text)
    {
        var tokens = WordRegex().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToArray();
        if (tokens.Length == 0) return false;
        for (int i = 0; i < tokens.Length;)
        {
            var phrase = Backchannels.FirstOrDefault(p => p.Length <= tokens.Length - i && p.AsSpan().SequenceEqual(tokens.AsSpan(i, p.Length)));
            if (phrase is null) return false;
            i += phrase.Length;
        }
        return true;
    }

    // Longest first, so "oh my god" is matched before "oh".
    private static readonly string[][] Backchannels =
        new[]
        {
            "yeah", "yes", "yep", "yup", "ya", "yah", "okay", "ok", "mm", "mhm", "mmhmm", "hmm", "uh huh", "mm hmm",
            "right", "sure", "totally", "exactly", "true", "wow", "oh", "ah", "huh", "cool", "nice", "great",
            "perfect", "absolutely", "definitely", "agreed", "no", "nope", "gotcha", "interesting", "alright",
            "all right", "i know", "i see", "for sure", "that's right", "that's true", "of course", "fair enough",
            "makes sense", "oh my god", "got it", "me too",
        }
        .Select(p => p.Split(' '))
        .OrderByDescending(p => p.Length)
        .ToArray();

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
            var word = source.Words[i];
            // A "yeah" under someone's long turn is the listener's, so it can become an interjection.
            int? floor = IsInterjection(word.Text) ? null : FloorHolder(word, source.OverlappingTurns);
            raw[i] = floor ?? SpeakerFor(word, turns) ?? previous;
            previous = raw[i];
        }
        // Words before the first matched turn inherit the first speaker that does match.
        int first = raw.FirstOrDefault(r => r >= 0, -1);
        for (int i = 0; i < raw.Length && raw[i] < 0; i++) raw[i] = first;
        AbsorbSlivers(raw, source.Words);

        var order = new Dictionary<int, int>();
        foreach (var r in raw)
            if (r >= 0 && !order.ContainsKey(r)) order[r] = order.Count + 1;

        for (int i = 0; i < raw.Length; i++)
            labels[i] = order.Count <= 1 || raw[i] < 0 ? source.Label : $"{source.Label} {order[raw[i]]}";
        return labels;
    }

    /// <summary>A fragment this short can be handed back to the speaker on either side of it.</summary>
    internal const double MaxSliverSeconds = 1.0;

    /// <summary>
    /// Gives short fragments back to the person who was talking. When two people talk at once, diarization
    /// gives each instant to one of them, so the talker's sentence comes out in slivers ("an intake | of |
    /// some kind"). A run of up to <see cref="MaxSliverSeconds"/> inside one Whisper segment, between two runs
    /// of the same other speaker, joins them. Shortest first, so a flurry of slivers resolves to whoever
    /// holds most of it. Remarks like "yep" are joined too, so they don't pull the slivers beside them
    /// their way, then given back to their speaker to become interjections.
    /// </summary>
    private static void AbsorbSlivers(int[] raw, IReadOnlyList<TimedWord> words)
    {
        var original = (int[])raw.Clone();
        while (JoinShortestSliver(raw, words)) { }

        for (int i = 0; i < raw.Length;)
        {
            int j = i;
            while (j + 1 < raw.Length && original[j + 1] == original[i]) j++;
            bool absorbed = raw[i] != original[i];
            bool brief = j - i + 1 <= MaxInterjectionWords && words[j].End - words[i].Start <= MaxInterjectionSeconds;
            if (absorbed && brief && IsInterjection(Join(words, i, j))) Array.Copy(original, i, raw, i, j - i + 1);
            i = j + 1;
        }

    }

    /// <summary>Gives the shortest sliver to the speaker around it; false when there are none left.</summary>
    private static bool JoinShortestSliver(int[] raw, IReadOnlyList<TimedWord> words)
    {
        // Runs of one speaker within one segment.
        var runs = new List<(int From, int To)>();
        for (int i = 0; i < raw.Length;)
        {
            int j = i;
            while (j + 1 < raw.Length && raw[j + 1] == raw[i] && words[j + 1].Segment == words[i].Segment) j++;
            runs.Add((i, j));
            i = j + 1;
        }

        (int From, int To, int Speaker)? pick = null;
        double shortest = MaxSliverSeconds;
        for (int r = 1; r < runs.Count - 1; r++)
        {
            var (from, to) = runs[r];
            int before = runs[r - 1].To, after = runs[r + 1].From;
            int segment = words[from].Segment;
            if (segment < 0 || words[before].Segment != segment || words[after].Segment != segment) continue;
            if (raw[before] != raw[after] || raw[before] == raw[from]) continue;
            double length = words[to].End - words[from].Start;
            if (length > shortest) continue;
            shortest = length;
            pick = (from, to, raw[before]);
        }
        if (pick is not { } p) return false;
        Array.Fill(raw, p.Speaker, p.From, p.To - p.From + 1);
        return true;
    }

    /// <summary>Shortest turn that can hold the floor.</summary>
    internal const double MinFloorSeconds = 2.0;

    /// <summary>How far another speaker's turn may stick out of the floor holder's and still count as inside it.</summary>
    internal const double FloorToleranceSeconds = 0.25;

    /// <summary>
    /// Who held the floor when the word was said, if two people's turns overlap there and one person's long
    /// turn contains all the others: someone said "mm-hmm" under the talker, and Whisper heard the talker.
    /// Null where turns don't overlap, or where neither contains the other (one person taking over from another).
    /// </summary>
    private static int? FloorHolder(TimedWord word, IReadOnlyList<SpeakerTurn>? overlapping)
    {
        if (overlapping is null) return null;
        var covering = overlapping.Where(t => Math.Min(word.End, t.End) - Math.Max(word.Start, t.Start) > 0).ToList();
        if (covering.Select(t => t.Speaker).Distinct().Count() < 2) return null;

        foreach (var holder in covering.Where(t => t.End - t.Start >= MinFloorSeconds).OrderByDescending(t => t.End - t.Start))
        {
            bool containsOthers = covering.All(t => t.Speaker == holder.Speaker
                || (t.Start >= holder.Start - FloorToleranceSeconds && t.End <= holder.End + FloorToleranceSeconds));
            if (containsOthers) return holder.Speaker;
        }
        return null;
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
