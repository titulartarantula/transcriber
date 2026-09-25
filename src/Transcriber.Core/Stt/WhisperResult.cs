namespace Transcriber.Core.Stt;

/// <summary>The verbose_json response shape shared by OpenAI and faster-whisper-server.</summary>
public sealed class WhisperResult
{
    public string Text { get; set; } = "";
    public string? Language { get; set; }
    public double Duration { get; set; }
    public List<WhisperSegment> Segments { get; set; } = new();
    public List<WhisperWord>? Words { get; set; }
}

public sealed class WhisperSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public double AvgLogprob { get; set; }
    public double CompressionRatio { get; set; }
    public double NoSpeechProb { get; set; }
    public List<WhisperWord>? Words { get; set; }
}

public sealed class WhisperWord
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Word { get; set; } = "";
    public double Probability { get; set; }
}
