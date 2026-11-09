using System.Globalization;
using System.Text.RegularExpressions;

namespace Transcriber.Core.Output;

public static partial class FileNames
{
    /// <summary>Expands {date}, {time} and {title} and strips characters Windows or Obsidian reject.</summary>
    public static string Build(string template, string title, DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(template)) template = "{date} {time} {title}";
        if (string.IsNullOrWhiteSpace(title)) title = "Transcript";

        var name = template
            .Replace("{date}", startedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", startedAt.ToString("HHmm", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase);

        // Windows-invalid characters plus the ones Obsidian refuses in note names (#^[]|).
        name = InvalidRegex().Replace(name, " ");
        name = SpaceRegex().Replace(name, " ").Trim().TrimEnd('.');
        if (name.Length > 120) name = name[..120].TrimEnd().TrimEnd('.');
        if (name.Length == 0) name = "Transcript";

        // CON, NUL, COM1… can't be file names on Windows, with any extension after them.
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).TrimEnd();
        if (ReservedRegex().IsMatch(stem)) name = stem + "_" + (dot < 0 ? "" : name[dot..]);
        return name + ".md";
    }

    /// <summary>
    /// A title as typed, made fit to store and show: line breaks and other control characters become spaces,
    /// runs of spaces collapse, and it's trimmed. File names are made safe separately, by <see cref="Build"/>.
    /// </summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var clean = SpaceRegex().Replace(ControlRegex().Replace(title, " "), " ").Trim();
        return clean.Length > 200 ? clean[..200].TrimEnd() : clean;
    }

    /// <summary>"name.md" → "name (2).md", "name (3).md", … until <paramref name="exists"/> says it is free.</summary>
    public static async Task<string> UniqueAsync(string fileName, Func<string, Task<bool>> exists)
    {
        if (!await exists(fileName)) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!await exists(candidate)) return candidate;
        }
    }

    [GeneratedRegex(@"[\\/:*?""<>|#^\[\]\x00-\x1F]")]
    private static partial Regex InvalidRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"[\x00-\x1F\x7F]")]
    private static partial Regex ControlRegex();

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedRegex();
}
