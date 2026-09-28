using System.Text.Json;
using System.Text.Json.Serialization;

namespace Transcriber.Core.Settings;

public enum OutputKind { MarkdownFolder, Obsidian, ObsidianMcp }

public sealed class AppSettings
{
    public SttSettings Stt { get; set; } = new();
    public SpeakerSettings Speakers { get; set; } = new();
    public OutputSettings Output { get; set; } = new();

    /// <summary>Remembered per-device choices, keyed by WASAPI endpoint id.</summary>
    public List<SourcePreference> Sources { get; set; } = new();

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(this, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;
}

public sealed class SttSettings
{
    /// <summary>Base URL of an OpenAI-compatible server (faster-whisper-server, Speaches, OpenAI, ...).</summary>
    public string BaseUrl { get; set; } = "http://localhost:8000";

    [JsonIgnore]
    public string ApiKey
    {
        get => Secret.Unprotect(ApiKeyProtected);
        set => ApiKeyProtected = Secret.Protect(value);
    }

    public string? ApiKeyProtected { get; set; }

    public string Model { get; set; } = "deepdml/faster-whisper-large-v3-turbo-ct2";

    /// <summary>ISO-639-1 code, or empty to let Whisper detect it.</summary>
    public string Language { get; set; } = "";

    /// <summary>Initial prompt: names and jargon Whisper should expect.</summary>
    public string Prompt { get; set; } = "";

    public bool VadFilter { get; set; } = true;

    public int TimeoutMinutes { get; set; } = 60;
}

public sealed class SpeakerSettings
{
    /// <summary>Master switch for diarization; each source also has its own toggle.</summary>
    public bool Diarize { get; set; } = true;

    /// <summary>
    /// Ask the STT server to separate voices when it offers /v1/audio/diarization; otherwise, or if that
    /// fails, separate them on this device.
    /// </summary>
    public bool UseServer { get; set; } = true;

    /// <summary>Speakers per diarized source, or 0 to estimate.</summary>
    public int ExpectedSpeakers { get; set; }

    /// <summary>On-device clustering distance used when the speaker count is estimated. Higher merges more.</summary>
    public float ClusterThreshold { get; set; } = 0.9f;

    public bool ReviewNames { get; set; } = true;

    /// <summary>Drop mic lines that duplicate system audio picked up from the speakers.</summary>
    public bool SuppressEcho { get; set; } = true;
}

public sealed class OutputSettings
{
    public OutputKind Destination { get; set; } = OutputKind.MarkdownFolder;

    public string MarkdownFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Transcripts");

    /// <summary>
    /// Obsidian Local REST API endpoint. The plugin serves HTTPS with a self-signed cert on 27124; a LAN
    /// address works once its certificate is pinned (see <see cref="ObsidianCertificate"/>).
    /// </summary>
    public string ObsidianUrl { get; set; } = "https://127.0.0.1:27124";

    [JsonIgnore]
    public string ObsidianApiKey
    {
        get => Secret.Unprotect(ObsidianApiKeyProtected);
        set => ObsidianApiKeyProtected = Secret.Protect(value);
    }

    public string? ObsidianApiKeyProtected { get; set; }

    /// <summary>
    /// SHA-256 fingerprint of the plugin's self-signed certificate, trusted for non-loopback URLs
    /// (another PC on the LAN or over WireGuard).
    /// </summary>
    public string? ObsidianCertificate { get; set; }

    /// <summary>Streamable HTTP endpoint of an Obsidian MCP server, e.g. https://example.com/mcp.</summary>
    public string McpUrl { get; set; } = "";

    /// <summary>Vault-relative folder for new notes (REST API and MCP).</summary>
    public string ObsidianFolder { get; set; } = "Transcripts";

    /// <summary>Tokens: {date} (yyyy-MM-dd), {time} (HHmm), {title}.</summary>
    public string FileNameTemplate { get; set; } = "{date} {time} {title}";

    /// <summary>Comma-separated tags written to frontmatter.</summary>
    public string Tags { get; set; } = "transcript";

    /// <summary>Write speakers as [[wikilinks]] so they connect to people notes.</summary>
    public bool LinkSpeakers { get; set; }

    /// <summary>Keep each recording's 16 kHz audio after the note is saved so it can be reprocessed.</summary>
    public bool KeepAudio { get; set; }
}

public sealed class SourcePreference
{
    public string DeviceId { get; set; } = "";
    public bool Selected { get; set; }
    public string Label { get; set; } = "";
    public bool Diarize { get; set; }
}
