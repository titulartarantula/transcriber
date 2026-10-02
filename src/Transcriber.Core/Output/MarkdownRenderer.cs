using System.Globalization;
using System.Text;
using Transcriber.Core.Transcript;

namespace Transcriber.Core.Output;

public sealed record NoteSource(string Label, string DeviceName);

public sealed record NoteData(
    string Title,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    IReadOnlyList<NoteSource> Sources,
    IReadOnlyList<Utterance> Utterances,
    string Model,
    string? Language,
    IReadOnlyList<string> Tags,
    bool LinkSpeakers)
{
    /// <summary>The app and version that made the note, e.g. "Transcriber for Android 0.2.0".</summary>
    public string? App { get; init; }
}

public static class MarkdownRenderer
{
    public static string Render(NoteData note)
    {
        var speakers = note.Utterances
            .SelectMany(u => u.Interjections.Select(x => x.Speaker).Prepend(u.Speaker))
            .Distinct()
            .ToList();
        var sb = new StringBuilder();

        sb.AppendLine("---");
        sb.AppendLine($"title: {Yaml(note.Title)}");
        sb.AppendLine($"date: {note.StartedAt:yyyy-MM-dd}");
        sb.AppendLine($"start: {Yaml(note.StartedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture))}");
        sb.AppendLine($"end: {Yaml(TimeOfDay(note.StartedAt, note.Duration.TotalSeconds))}");
        sb.AppendLine($"duration: {Yaml(Clock(note.Duration))}");
        sb.AppendLine("type: transcript");
        if (!string.IsNullOrEmpty(note.App)) sb.AppendLine($"app: {Yaml(note.App)}");
        AppendList(sb, "speakers", speakers.Select(s => note.LinkSpeakers ? $"[[{s}]]" : s));
        AppendList(sb, "sources", note.Sources.Select(s => $"{s.Label} ({s.DeviceName})"));
        sb.AppendLine($"stt_model: {Yaml(note.Model)}");
        if (!string.IsNullOrEmpty(note.Language)) sb.AppendLine($"language: {Yaml(note.Language)}");
        AppendList(sb, "tags", note.Tags);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {note.Title}");
        sb.AppendLine();

        if (note.Utterances.Count == 0)
        {
            sb.AppendLine("_No speech was detected._");
            return sb.ToString();
        }

        foreach (var u in note.Utterances)
        {
            var name = note.LinkSpeakers ? $"[[{u.Speaker}]]" : u.Speaker;
            sb.AppendLine($"**[{TimeOfDay(note.StartedAt, u.Start)}] {name}:** {WithInterjections(u, note.LinkSpeakers)}");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>"we need data (Sam: yep) for the intake". A remark by the speaker themselves (two clusters
    /// given the same name) just reads as part of what they said.</summary>
    private static string WithInterjections(Utterance u, bool link)
    {
        if (u.Interjections.Count == 0) return u.Text;
        var sb = new StringBuilder();
        int at = 0;
        foreach (var x in u.Interjections)
        {
            int offset = Math.Clamp(x.Offset, at, u.Text.Length);
            sb.Append(u.Text, at, offset - at);
            if (x.Speaker == u.Speaker) sb.Append(' ').Append(x.Text);
            else sb.Append(" (").Append(link ? $"[[{x.Speaker}]]" : x.Speaker).Append(": ").Append(x.Text).Append(')');
            at = offset;
        }
        return sb.Append(u.Text, at, u.Text.Length - at).ToString();
    }

    /// <summary>Wall-clock time of a point in the recording, 24-hour HH:mm:ss.</summary>
    public static string TimeOfDay(DateTimeOffset startedAt, double offsetSeconds) =>
        startedAt.AddSeconds(offsetSeconds).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Clock(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static void AppendList(StringBuilder sb, string key, IEnumerable<string> items)
    {
        var list = items.Where(i => !string.IsNullOrWhiteSpace(i)).ToList();
        if (list.Count == 0)
        {
            sb.AppendLine($"{key}: []");
            return;
        }
        sb.AppendLine($"{key}:");
        foreach (var item in list) sb.AppendLine($"  - {Yaml(item)}");
    }

    /// <summary>Double-quoted YAML scalar; always quoting keeps colons, # and [[links]] safe.</summary>
    internal static string Yaml(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", " ") + "\"";
}
