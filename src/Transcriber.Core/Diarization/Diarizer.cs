using SherpaOnnx;
using Transcriber.Core.Audio;

namespace Transcriber.Core.Diarization;

/// <summary>A stretch of audio attributed to one anonymous speaker.</summary>
public sealed record SpeakerTurn(double Start, double End, int Speaker);

/// <param name="Turns">One speaker at a time.</param>
/// <param name="Overlapping">The same speakers' turns allowed to overlap, when the diarizer reports them.</param>
public sealed record DiarizationTurns(IReadOnlyList<SpeakerTurn> Turns, IReadOnlyList<SpeakerTurn>? Overlapping);

/// <summary>Offline speaker diarization: pyannote segmentation plus embedding clustering, on the CPU.</summary>
public static class Diarizer
{
    /// <param name="samples">16 kHz mono audio.</param>
    /// <param name="expectedSpeakers">Known speaker count, or 0 to estimate from <paramref name="threshold"/>.</param>
    public static IReadOnlyList<SpeakerTurn> Run(float[] samples, int expectedSpeakers, float threshold,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!DiarizationModels.Present)
            throw new InvalidOperationException("Diarization models are missing; call DiarizationModels.EnsureAsync first.");

        int threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = DiarizationModels.SegmentationPath;
        config.Segmentation.NumThreads = threads;
        config.Embedding.Model = DiarizationModels.EmbeddingPath;
        config.Embedding.NumThreads = threads;
        config.Clustering.NumClusters = expectedSpeakers > 0 ? expectedSpeakers : -1;
        config.Clustering.Threshold = threshold;
        config.MinDurationOn = 0.3f;
        config.MinDurationOff = 0.5f;

        var sd = new OfflineSpeakerDiarization(config);
        if (sd.SampleRate != AudioConvert.SampleRate)
            throw new InvalidOperationException($"Diarization model expects {sd.SampleRate} Hz audio.");

        var callback = new OfflineSpeakerDiarizationProgressCallback((done, total, _) =>
        {
            if (total > 0) progress?.Report((double)done / total);
            // A non-zero return asks sherpa-onnx to stop early.
            return ct.IsCancellationRequested ? 1 : 0;
        });

        var segments = sd.ProcessWithCallback(samples, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        ct.ThrowIfCancellationRequested();

        return segments.Select(s => new SpeakerTurn(s.Start, s.End, s.Speaker)).OrderBy(t => t.Start).ToList();
    }
}
