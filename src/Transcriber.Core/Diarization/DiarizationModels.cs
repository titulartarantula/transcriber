namespace Transcriber.Core.Diarization;

/// <summary>Downloads the two ONNX models sherpa-onnx needs, once, into %LOCALAPPDATA%.</summary>
public static class DiarizationModels
{
    private const string SegmentationUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx";

    private const string EmbeddingUrl =
        "https://github.com/k2-fsa/sherpa-onnx/releases/download/speaker-recongition-models/3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx";

    public static string SegmentationPath => Path.Combine(AppPaths.Models, "pyannote-segmentation-3.0.onnx");

    public static string EmbeddingPath => Path.Combine(AppPaths.Models, "3dspeaker-eres2net-en-voxceleb.onnx");

    public static bool Present => File.Exists(SegmentationPath) && File.Exists(EmbeddingPath);

    public static async Task EnsureAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (Present) return;
        Directory.CreateDirectory(AppPaths.Models);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        await DownloadAsync(http, SegmentationUrl, SegmentationPath, "speaker segmentation model", progress, ct);
        await DownloadAsync(http, EmbeddingUrl, EmbeddingPath, "speaker embedding model", progress, ct);
    }

    private static async Task DownloadAsync(HttpClient http, string url, string path, string what,
        IProgress<string>? progress, CancellationToken ct)
    {
        if (File.Exists(path)) return;
        progress?.Report($"Downloading {what} (first run only)…");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        var tmp = path + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(tmp))
        {
            var buffer = new byte[81920];
            long done = 0;
            int lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total is > 0)
                {
                    int percent = (int)(done * 100 / total.Value);
                    if (percent / 10 != lastPercent / 10)
                    {
                        lastPercent = percent;
                        progress?.Report($"Downloading {what} (first run only)… {percent}%");
                    }
                }
            }
        }
        File.Move(tmp, path, overwrite: true);
    }
}
