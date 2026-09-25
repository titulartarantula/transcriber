using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Transcriber.Core.Settings;

namespace Transcriber.Core.Output;

public sealed record SavedNote(string Description, string? LocalPath);

public interface INoteDestination
{
    Task<SavedNote> SaveAsync(string fileName, string markdown, CancellationToken ct);
}

public static class NoteDestinations
{
    public static INoteDestination Create(OutputSettings s) => s.Destination switch
    {
        OutputKind.Obsidian => new ObsidianDestination(s.ObsidianUrl, s.ObsidianApiKey, s.ObsidianFolder, s.ObsidianCertificate),
        OutputKind.ObsidianMcp => new Mcp.McpDestination(s.McpUrl, s.ObsidianFolder),
        _ => new FolderDestination(s.MarkdownFolder),
    };
}

public sealed class FolderDestination(string folder) : INoteDestination
{
    public async Task<SavedNote> SaveAsync(string fileName, string markdown, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("No output folder is configured.");
        Directory.CreateDirectory(folder);
        var unique = await FileNames.UniqueAsync(fileName, n => Task.FromResult(File.Exists(Path.Combine(folder, n))));
        var path = Path.Combine(folder, unique);
        await File.WriteAllTextAsync(path, markdown, new UTF8Encoding(false), ct);
        return new SavedNote(path, path);
    }
}

/// <summary>
/// The server presented a certificate Windows doesn't trust and that isn't the pinned one. The plugin's
/// certificate is self-signed and names only 127.0.0.1, so reaching Obsidian over the LAN or a VPN means
/// pinning its fingerprint once.
/// </summary>
public sealed class UntrustedCertificateException(string host, string fingerprint, bool pinChanged)
    : Exception(pinChanged
        ? $"The certificate presented by {host} has changed since you trusted it (now {ObsidianDestination.Format(fingerprint)}). " +
          "If you regenerated it in Obsidian, test the connection in Settings to trust the new one."
        : $"{host} presented a self-signed certificate that hasn't been trusted yet. Test the connection in Settings to trust it.")
{
    public string Host { get; } = host;

    /// <summary>SHA-256 of the presented certificate, uppercase hex.</summary>
    public string Fingerprint { get; } = fingerprint;

    public bool PinChanged { get; } = pinChanged;
}

/// <summary>Writes notes through the Obsidian Local REST API plugin (coddingtonbear/obsidian-local-rest-api).</summary>
public sealed class ObsidianDestination : INoteDestination, IDisposable
{
    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly string _host;
    private readonly string? _pinned;
    private string? _rejected;

    /// <param name="pinnedCertificate">SHA-256 fingerprint of the one certificate to accept even though Windows doesn't trust it.</param>
    public ObsidianDestination(string baseUrl, string apiKey, string folder, string? pinnedCertificate = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("The Obsidian REST API key is not set.");
        if (!Uri.TryCreate(baseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"'{baseUrl}' is not a valid Obsidian REST API URL.");

        _host = baseUri.Authority;
        _pinned = Normalize(pinnedCertificate);
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, cert, _, errors) => Validate(baseUri, cert, errors) };

        _http = new HttpClient(handler) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        _folder = folder.Trim().Trim('/', '\\').Replace('\\', '/');
    }

    /// <summary>Checks the plugin is reachable and the key is accepted.</summary>
    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        using var response = await Send(() => _http.GetAsync("", ct));
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Obsidian returned {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        bool authenticated = doc.RootElement.TryGetProperty("authenticated", out var a) && a.GetBoolean();
        if (!authenticated) throw new InvalidOperationException("Obsidian is reachable but rejected the API key.");
        var version = doc.RootElement.TryGetProperty("versions", out var v) && v.TryGetProperty("obsidian", out var o)
            ? o.GetString() : null;
        return version is null ? "Connected to Obsidian." : $"Connected to Obsidian {version}.";
    }

    public async Task<SavedNote> SaveAsync(string fileName, string markdown, CancellationToken ct)
    {
        var unique = await FileNames.UniqueAsync(fileName, async n =>
        {
            using var r = await Send(() => _http.GetAsync(VaultPath(n), ct));
            return r.StatusCode != HttpStatusCode.NotFound;
        });

        var content = new StringContent(markdown, new UTF8Encoding(false));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/markdown") { CharSet = "utf-8" };
        using var response = await Send(() => _http.PutAsync(VaultPath(unique), content, ct));
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Obsidian refused the note ({(int)response.StatusCode}): {body}");
        }

        var vaultPath = _folder.Length == 0 ? unique : $"{_folder}/{unique}";
        return new SavedNote($"Obsidian vault: {vaultPath}", null);
    }

    private string VaultPath(string fileName)
    {
        var segments = _folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Append(fileName);
        return "vault/" + string.Join('/', segments.Select(Uri.EscapeDataString));
    }

    /// <summary>Standard validation first; then loopback (nobody to impersonate) or an exact fingerprint match.</summary>
    private bool Validate(Uri baseUri, System.Security.Cryptography.X509Certificates.X509Certificate2? cert, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (cert is null) return false;
        if (baseUri.IsLoopback) return true;

        var fingerprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
        if (_pinned is not null && string.Equals(fingerprint, _pinned, StringComparison.OrdinalIgnoreCase)) return true;
        _rejected = fingerprint;
        return false;
    }

    /// <summary>"AED25E…" → "AE:D2:5E:…", the form certificate viewers show.</summary>
    public static string Format(string fingerprint) =>
        string.Join(':', Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));

    private static string? Normalize(string? fingerprint)
    {
        var hex = fingerprint?.Replace(":", "").Replace(" ", "").Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(hex) ? null : hex;
    }

    private async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> request)
    {
        try
        {
            return await request();
        }
        catch (HttpRequestException) when (_rejected is not null)
        {
            throw new UntrustedCertificateException(_host, _rejected, pinChanged: _pinned is not null);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException(
                $"Could not reach Obsidian's Local REST API. Is Obsidian open with the plugin enabled? ({e.Message})", e);
        }
    }

    public void Dispose() => _http.Dispose();
}
