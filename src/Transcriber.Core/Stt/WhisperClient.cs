using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Transcriber.Core.Diarization;
using Transcriber.Core.Settings;

namespace Transcriber.Core.Stt;

public class SttException(string message) : Exception(message);

public sealed class DiarizationUnsupportedException()
    : SttException("The STT server does not offer speaker separation.");

/// <summary>Client for the OpenAI-compatible /v1/audio/transcriptions endpoint.</summary>
public sealed class WhisperClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly SttSettings _settings;

    public WhisperClient(SttSettings settings)
    {
        _settings = settings;
        if (!Uri.TryCreate(settings.BaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            throw new SttException($"'{settings.BaseUrl}' is not a valid server URL.");

        _http = new HttpClient
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromMinutes(Math.Max(1, settings.TimeoutMinutes)),
        };
        var key = settings.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/models", ct);
        await EnsureSuccess(response, ct);
        var list = await response.Content.ReadFromJsonAsync<ModelList>(Json, ct);
        return list?.Data.Select(m => m.Id).ToList() ?? [];
    }

    /// <summary>True when the server offers the /v1/audio/diarization extension (see the whisper-server project).</summary>
    public async Task<bool> SupportsDiarizationAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("v1/audio/diarization", ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Speaker turns from the server's diarization endpoint.</summary>
    /// <param name="expectedSpeakers">Known speaker count, or 0 to let the server estimate it.</param>
    /// <exception cref="DiarizationUnsupportedException">The server has no diarization endpoint.</exception>
    public async Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(string wavPath, int expectedSpeakers, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(WavContent(wavPath), "file", Path.GetFileName(wavPath));
        if (expectedSpeakers > 0) form.Add(new StringContent(expectedSpeakers.ToString()), "num_speakers");

        using var response = await PostAsync("v1/audio/diarization", form, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
            throw new DiarizationUnsupportedException();
        await EnsureSuccess(response, ct);

        var result = await response.Content.ReadFromJsonAsync<DiarizationResult>(Json, ct)
            ?? throw new SttException("The server returned an empty diarization response.");
        return result.Segments.Select(s => new SpeakerTurn(s.Start, s.End, s.Speaker)).OrderBy(t => t.Start).ToList();
    }

    public async Task<WhisperResult> TranscribeAsync(string wavPath, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(WavContent(wavPath), "file", Path.GetFileName(wavPath));
        form.Add(new StringContent(_settings.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("word"), "timestamp_granularities[]");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        form.Add(new StringContent("0"), "temperature");
        if (_settings.VadFilter) form.Add(new StringContent("true"), "vad_filter");
        if (!string.IsNullOrWhiteSpace(_settings.Language)) form.Add(new StringContent(_settings.Language.Trim()), "language");
        if (!string.IsNullOrWhiteSpace(_settings.Prompt)) form.Add(new StringContent(_settings.Prompt.Trim()), "prompt");

        using var response = await PostAsync("v1/audio/transcriptions", form, ct);
        await EnsureSuccess(response, ct);
        return await response.Content.ReadFromJsonAsync<WhisperResult>(Json, ct)
            ?? throw new SttException("The STT server returned an empty response.");
    }

    private static StreamContent WavContent(string wavPath)
    {
        var file = new StreamContent(File.OpenRead(wavPath));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return file;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, HttpContent content, CancellationToken ct)
    {
        try
        {
            return await _http.PostAsync(path, content, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SttException($"The STT server did not answer within {_settings.TimeoutMinutes} minutes.");
        }
        catch (HttpRequestException e)
        {
            throw new SttException($"Could not reach the STT server at {_http.BaseAddress}: {e.Message}");
        }
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 500) body = body[..500] + "…";
        throw new SttException($"STT server returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    public void Dispose() => _http.Dispose();

    private sealed record ModelList(List<ModelEntry> Data);

    private sealed record ModelEntry(string Id);

    private sealed record DiarizationResult(List<DiarizationSegment> Segments);

    private sealed record DiarizationSegment(double Start, double End, int Speaker);
}
