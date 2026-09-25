using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Transcriber.Core.Settings;

namespace Transcriber.Core.Stt;

public sealed class SttException(string message) : Exception(message);

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

    public async Task<WhisperResult> TranscribeAsync(string wavPath, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(File.OpenRead(wavPath));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent(_settings.Model), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("word"), "timestamp_granularities[]");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");
        form.Add(new StringContent("0"), "temperature");
        if (_settings.VadFilter) form.Add(new StringContent("true"), "vad_filter");
        if (!string.IsNullOrWhiteSpace(_settings.Language)) form.Add(new StringContent(_settings.Language.Trim()), "language");
        if (!string.IsNullOrWhiteSpace(_settings.Prompt)) form.Add(new StringContent(_settings.Prompt.Trim()), "prompt");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("v1/audio/transcriptions", form, ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SttException($"The STT server did not answer within {_settings.TimeoutMinutes} minutes.");
        }
        catch (HttpRequestException e)
        {
            throw new SttException($"Could not reach the STT server at {_http.BaseAddress}: {e.Message}");
        }

        using (response)
        {
            await EnsureSuccess(response, ct);
            return await response.Content.ReadFromJsonAsync<WhisperResult>(Json, ct)
                ?? throw new SttException("The STT server returned an empty response.");
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
}
