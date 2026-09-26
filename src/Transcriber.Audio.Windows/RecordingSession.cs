using System.Diagnostics;
using Transcriber.Core;
using Transcriber.Core.Audio;

namespace Transcriber.Audio.Windows;

public sealed record SourceSelection(AudioDeviceInfo Device, string Label, bool Diarize);

/// <summary>Records several endpoints at once, one WAV per endpoint, all on the same timeline.</summary>
public sealed class RecordingSession : IDisposable
{
    private readonly Stopwatch _clock;
    private readonly List<(SourceSelection Selection, SourceRecorder Recorder)> _sources;
    private readonly string _title;
    private readonly DateTimeOffset _startedAt;
    private readonly string _directory;

    private RecordingSession(string title, string directory, DateTimeOffset startedAt, Stopwatch clock,
        List<(SourceSelection, SourceRecorder)> sources)
    {
        _title = title;
        _directory = directory;
        _startedAt = startedAt;
        _clock = clock;
        _sources = sources;
    }

    public TimeSpan Elapsed => _clock.Elapsed;

    public static RecordingSession Start(string title, IReadOnlyList<SourceSelection> selections, string? rootDirectory = null)
    {
        if (selections.Count == 0) throw new ArgumentException("Select at least one audio source.", nameof(selections));

        var directory = Path.Combine(rootDirectory ?? AppPaths.Recordings, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(directory);

        var clock = new Stopwatch();
        DateTimeOffset startedAt;
        var sources = new List<(SourceSelection, SourceRecorder)>();
        try
        {
            for (int i = 0; i < selections.Count; i++)
            {
                var sel = selections[i];
                var path = Path.Combine(directory, $"source{i + 1}-{sel.Device.Kind}.wav");
                sources.Add((sel, new SourceRecorder(sel.Device.Id, sel.Device.Kind, path, clock)));
            }

            // Opening devices takes a moment; wall-clock time for offset 0 is when the shared clock starts.
            startedAt = DateTimeOffset.Now;
            clock.Start();
            foreach (var (_, recorder) in sources) recorder.Start();
        }
        catch
        {
            foreach (var (_, recorder) in sources) recorder.Dispose();
            throw;
        }

        return new RecordingSession(title, directory, startedAt, clock, sources);
    }

    /// <summary>Current peak per source, in selection order.</summary>
    public float[] ReadPeaks() => _sources.Select(s => s.Recorder.ReadPeak()).ToArray();

    public async Task<SessionRecording> StopAsync()
    {
        _clock.Stop();
        var length = _clock.Elapsed;
        await Task.WhenAll(_sources.Select(s => s.Recorder.StopAsync(length)));

        var recorded = _sources
            .Select(s => new RecordedSource(s.Recorder.FilePath, s.Selection.Label, s.Selection.Device.Kind,
                s.Selection.Diarize, s.Selection.Device.Name))
            .ToList();

        Dispose();
        return new SessionRecording(_title, _startedAt, length, _directory, recorded);
    }

    /// <summary>Devices that dropped out during the session, with the reason.</summary>
    public IEnumerable<(string Device, Exception Error)> Failures =>
        _sources.Where(s => s.Recorder.Error is not null).Select(s => (s.Selection.Device.Name, s.Recorder.Error!));

    public void Dispose()
    {
        foreach (var (_, recorder) in _sources) recorder.Dispose();
    }
}
