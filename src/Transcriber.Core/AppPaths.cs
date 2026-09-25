namespace Transcriber.Core;

public static class AppPaths
{
    public static string Roaming { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Transcriber");

    public static string Local { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Transcriber");

    public static string SettingsFile => Path.Combine(Roaming, "settings.json");

    /// <summary>Raw per-source WAVs live here until a transcript is delivered.</summary>
    public static string Recordings => Path.Combine(Local, "recordings");

    public static string Models => Path.Combine(Local, "models");

    /// <summary>Notes that could not be delivered to Obsidian are parked here.</summary>
    public static string Unsent => Path.Combine(Local, "unsent");
}
