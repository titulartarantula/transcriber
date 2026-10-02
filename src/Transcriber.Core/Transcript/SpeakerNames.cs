using System.Text.RegularExpressions;

namespace Transcriber.Core.Transcript;

public static partial class SpeakerNames
{
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "January", "February", "March", "April", "May", "June", "July", "August", "September",
        "October", "November", "December", "English", "Canadian", "American", "British", "Australian",
        "Sorry", "Okay", "Just", "Here", "Back", "Going", "Not", "Also", "Actually", "Really", "The",
        "Microsoft", "Google", "Zoom", "Teams",
    };

    /// <summary>
    /// Summarizes each speaker and guesses real names for the generic ones ("Remote 2") from what
    /// people say: self-introductions ("Hi, I'm Priya", "this is Tom from finance") and greetings
    /// that name whoever answers next ("Thanks, Sanjay.").
    /// </summary>
    public static List<SpeakerSummary> Summarize(IReadOnlyList<Utterance> utterances, ISet<string> genericSpeakers)
    {
        var votes = new Dictionary<string, Dictionary<string, int>>();
        void Vote(string speaker, string name, int weight)
        {
            if (!genericSpeakers.Contains(speaker)) return;
            var tally = votes.TryGetValue(speaker, out var t) ? t : votes[speaker] = new();
            tally[name] = tally.GetValueOrDefault(name) + weight;
        }

        for (int i = 0; i < utterances.Count; i++)
        {
            var u = utterances[i];
            // Saying your own name is stronger evidence than being addressed.
            foreach (var name in Introductions(u.Text)) Vote(u.Speaker, name, 2);

            if (Addressee(u.Text) is { } addressed
                && utterances.Skip(i + 1).FirstOrDefault(n => n.Speaker != u.Speaker) is { } reply
                && reply.Start - u.End < 15)
            {
                Vote(reply.Speaker, addressed, 1);
            }
        }

        // One name per speaker, and never the same name for two speakers.
        var suggestions = new Dictionary<string, string>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (speaker, tally) in votes.OrderByDescending(v => v.Value.Values.Max()))
        {
            var pick = tally.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault(n => !taken.Contains(n));
            if (pick is null) continue;
            suggestions[speaker] = pick;
            taken.Add(pick);
        }

        var summaries = utterances
            .GroupBy(u => u.Speaker)
            .OrderBy(g => g.Min(u => u.Start))
            .Select(g => new SpeakerSummary(
                g.Key,
                genericSpeakers.Contains(g.Key),
                TimeSpan.FromSeconds(g.Sum(u => u.Duration)),
                Truncate(g.MaxBy(u => u.Text.Length)!.Text, 160),
                suggestions.GetValueOrDefault(g.Key)))
            .ToList();

        // Someone who only ever chimed in ("yep") still needs a name.
        var onlyInterjected = utterances
            .SelectMany(u => u.Interjections)
            .GroupBy(x => x.Speaker)
            .Where(g => summaries.All(s => s.Speaker != g.Key));
        foreach (var g in onlyInterjected)
        {
            summaries.Add(new SpeakerSummary(g.Key, genericSpeakers.Contains(g.Key), TimeSpan.Zero,
                Truncate(g.MaxBy(x => x.Text.Length)!.Text, 160), suggestions.GetValueOrDefault(g.Key)));
        }
        return summaries;
    }

    internal static IEnumerable<string> Introductions(string text)
    {
        foreach (Match m in IntroRegex().Matches(text))
        {
            // Introductions come at the start of what someone says, not buried mid-sentence.
            if (m.Index > 80) continue;
            var name = m.Groups["name"].Value;
            if (!NotNames.Contains(name)) yield return name;
        }
    }

    /// <summary>A name right after an opening greeting or thanks: "Hi, Sanjay." / "Thanks Tom, ...".</summary>
    internal static string? Addressee(string text)
    {
        var m = GreetingRegex().Match(text);
        return m.Success && !NotNames.Contains(m.Groups["name"].Value) ? m.Groups["name"].Value : null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    // "my name is" is case-insensitive; the name itself must be capitalised, which Whisper does for proper nouns.
    [GeneratedRegex(@"\b(?i:my name is|my name's|i'm|i am|this is|it's)\s+(?<name>[A-Z][a-z]{1,20})\b(?!['’]s)")]
    private static partial Regex IntroRegex();

    [GeneratedRegex(@"^\W*(?i:hi|hey|hello|morning|good morning|thanks|thank you|over to you|go ahead)[,!.]?\s+(?<name>[A-Z][a-z]{1,20})\b(?=\s*[,.!?]|\s*$)")]
    private static partial Regex GreetingRegex();
}
