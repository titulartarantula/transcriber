using System.Text.Json;
using Transcriber.Core.Pipeline;
using Transcriber.Core.Settings;

namespace Transcriber.Core.Jobs;

/// <summary>Reads and writes each job's job.json and draft.json inside its recording folder.</summary>
public sealed class JobStore(string root)
{
    private const string JobFile = "job.json";
    private const string DraftFile = "draft.json";

    public string Root => root;

    /// <summary>Every job under the root. Folders without a readable job.json are left alone.</summary>
    public List<TranscriptionJob> LoadAll()
    {
        var jobs = new List<TranscriptionJob>();
        if (!Directory.Exists(root)) return jobs;

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var path = Path.Combine(directory, JobFile);
            if (!File.Exists(path)) continue;
            try
            {
                var job = JsonSerializer.Deserialize<TranscriptionJob>(File.ReadAllText(path), SettingsStore.JsonOptions);
                // Trust where the file is over where it says it is.
                if (job is not null)
                    jobs.Add(job with { Id = Path.GetFileName(directory), Recording = job.Recording with { Directory = directory } });
            }
            catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
            {
            }
        }
        return jobs;
    }

    public void Save(TranscriptionJob job) => Write(Path.Combine(job.Recording.Directory, JobFile), job);

    public void SaveDraft(TranscriptionJob job, TranscriptDraft draft) =>
        Write(Path.Combine(job.Recording.Directory, DraftFile), draft);

    public TranscriptDraft? LoadDraft(TranscriptionJob job)
    {
        var path = Path.Combine(job.Recording.Directory, DraftFile);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<TranscriptDraft>(File.ReadAllText(path), SettingsStore.JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void DeleteDraft(TranscriptionJob job) => File.Delete(Path.Combine(job.Recording.Directory, DraftFile));

    private static void Write<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, SettingsStore.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
