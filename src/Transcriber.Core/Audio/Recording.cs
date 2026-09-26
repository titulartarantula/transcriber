namespace Transcriber.Core.Audio;

public enum SourceKind
{
    /// <summary>A capture endpoint: microphone, headset, line-in.</summary>
    Microphone,

    /// <summary>A render endpoint recorded through WASAPI loopback: whatever plays on that output.</summary>
    SystemAudio,

    /// <summary>An existing audio file handed to the pipeline.</summary>
    File,
}

public sealed record RecordedSource(string FilePath, string Label, SourceKind Kind, bool Diarize, string DeviceName);

/// <summary>A finished recording: one audio file per source, all starting at <see cref="StartedAt"/>.</summary>
public sealed record SessionRecording(
    string Title,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    string Directory,
    IReadOnlyList<RecordedSource> Sources);
