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
        if (name.Length > 120) name = name[..120].TrimEnd();
        return (name.Length == 0 ? "Transcript" : name) + ".md";
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
}
