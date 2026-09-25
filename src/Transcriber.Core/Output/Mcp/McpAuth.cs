using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Web;
using Transcriber.Core.Settings;

namespace Transcriber.Core.Output.Mcp;

public sealed class McpAuthException(string message) : Exception(message);

/// <summary>Tokens and client registration for one MCP server, persisted between runs.</summary>
public sealed class McpCredentials
{
    public string ServerUrl { get; set; } = "";
    public string Resource { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string? ClientSecret { get; set; }
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonIgnore]
    public bool NeedsRefresh => ExpiresAt is { } e && e - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(1);
}

/// <summary>DPAPI-encrypted credential file in %APPDATA%. One MCP server at a time.</summary>
public static class McpCredentialStore
{
    /// <summary>Lets tests keep their credentials away from the real store.</summary>
    internal static string? PathOverride { get; set; }

    private static string FilePath => PathOverride ?? Path.Combine(AppPaths.Roaming, "mcp-auth.dat");

    public static McpCredentials? Load(string serverUrl)
    {
        if (!File.Exists(FilePath)) return null;
        var json = Secret.Unprotect(File.ReadAllText(FilePath));
        if (json.Length == 0) return null;
        var creds = JsonSerializer.Deserialize<McpCredentials>(json);
        return creds is not null && SameServer(creds.ServerUrl, serverUrl) ? creds : null;
    }

    public static void Save(McpCredentials creds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, Secret.Protect(JsonSerializer.Serialize(creds)));
    }

    public static void Clear()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    private static bool SameServer(string a, string b) =>
        string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// OAuth 2.1 for MCP as a native app: protected-resource discovery, dynamic client registration,
/// authorization code + PKCE through the system browser with a loopback redirect (RFC 8252), and refresh.
/// </summary>
public static class McpAuth
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Runs the interactive sign-in and stores the resulting credentials.</summary>
    /// <param name="openBrowser">Opens the authorization URL for the user.</param>
    public static async Task<McpCredentials> SignInAsync(string serverUrl, Action<Uri> openBrowser, CancellationToken ct)
    {
        var server = ParseServer(serverUrl);
        var (resource, authServer) = await DiscoverResourceAsync(server, ct);
        var meta = await GetJsonAsync(new Uri(authServer, "/.well-known/oauth-authorization-server"), ct)
            ?? await GetJsonAsync(new Uri(authServer, "/.well-known/openid-configuration"), ct)
            ?? throw new McpAuthException($"{authServer.Host} doesn't publish OAuth metadata.");

        var authorizeEndpoint = Required(meta, "authorization_endpoint");
        var tokenEndpoint = Required(meta, "token_endpoint");
        var registrationEndpoint = meta["registration_endpoint"]?.GetValue<string>()
            ?? throw new McpAuthException("The MCP server doesn't support client registration, so the app can't sign in to it.");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/callback";

        var (clientId, clientSecret) = await RegisterAsync(registrationEndpoint, redirect, ct);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));
        var query = HttpUtility.ParseQueryString("");
        query["response_type"] = "code";
        query["client_id"] = clientId;
        query["redirect_uri"] = redirect;
        query["scope"] = "mcp";
        query["state"] = state;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";
        query["resource"] = resource;
        openBrowser(new Uri($"{authorizeEndpoint}?{query}"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var callback = await WaitForCallbackAsync(listener, timeout.Token);

        if (callback["error"] is { } error)
            throw new McpAuthException($"Sign-in was refused: {callback["error_description"] ?? error}");
        if (callback["state"] != state)
            throw new McpAuthException("Sign-in response didn't match the request (state mismatch). Try again.");
        var code = callback["code"] ?? throw new McpAuthException("The sign-in response had no authorization code.");

        var creds = new McpCredentials
        {
            ServerUrl = serverUrl.Trim(),
            Resource = resource,
            TokenEndpoint = tokenEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
        };
        await RequestTokenAsync(creds, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirect,
            ["code_verifier"] = verifier,
        }, ct);
        McpCredentialStore.Save(creds);
        return creds;
    }

    /// <summary>Exchanges the refresh token for a new access token and persists it.</summary>
    public static async Task RefreshAsync(McpCredentials creds, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(creds.RefreshToken))
            throw new McpAuthException("The MCP sign-in has expired. Sign in again in Settings.");
        try
        {
            await RequestTokenAsync(creds, new()
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = creds.RefreshToken,
            }, ct);
        }
        catch (McpAuthException e)
        {
            throw new McpAuthException($"The MCP sign-in has expired ({e.Message}). Sign in again in Settings.");
        }
        McpCredentialStore.Save(creds);
    }

    private static async Task RequestTokenAsync(McpCredentials creds, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = creds.ClientId;
        form["resource"] = creds.Resource;
        if (creds.ClientSecret is not null) form["client_secret"] = creds.ClientSecret;

        using var response = await Http.PostAsync(creds.TokenEndpoint, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(body); } catch (JsonException) { }

        if (!response.IsSuccessStatusCode || json?["access_token"] is null)
        {
            var reason = json?["error_description"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>() ?? $"HTTP {(int)response.StatusCode}";
            throw new McpAuthException(reason);
        }

        creds.AccessToken = json["access_token"]!.GetValue<string>();
        creds.RefreshToken = json["refresh_token"]?.GetValue<string>() ?? creds.RefreshToken;
        creds.ExpiresAt = json["expires_in"] is { } exp ? DateTimeOffset.UtcNow.AddSeconds(exp.GetValue<double>()) : null;
    }

    private static async Task<(string ClientId, string? Secret)> RegisterAsync(string endpoint, string redirect, CancellationToken ct)
    {
        var request = new
        {
            client_name = "Transcriber",
            redirect_uris = new[] { redirect },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
            scope = "mcp",
        };
        using var response = await Http.PostAsJsonAsync(endpoint, request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new McpAuthException($"Client registration failed ({(int)response.StatusCode}): {body}");
        var json = JsonNode.Parse(body)!;
        return (Required(json, "client_id"), json["client_secret"]?.GetValue<string>());
    }

    /// <summary>
    /// Finds the protected-resource metadata (RFC 9728): path-specific first, then the host root,
    /// and returns the canonical resource id plus its authorization server.
    /// </summary>
    private static async Task<(string Resource, Uri AuthServer)> DiscoverResourceAsync(Uri server, CancellationToken ct)
    {
        var path = server.AbsolutePath.TrimEnd('/');
        var candidates = new List<Uri> { new(server, "/.well-known/oauth-protected-resource" + path) };
        if (path.Length > 0) candidates.Add(new Uri(server, "/.well-known/oauth-protected-resource"));

        foreach (var url in candidates)
        {
            if (await GetJsonAsync(url, ct) is not { } meta) continue;
            var authServer = meta["authorization_servers"]?.AsArray().FirstOrDefault()?.GetValue<string>();
            if (authServer is null) continue;
            return (meta["resource"]?.GetValue<string>() ?? server.ToString(), new Uri(authServer));
        }
        // Older servers: the MCP host is its own authorization server.
        return (server.ToString(), new Uri(server.GetLeftPart(UriPartial.Authority)));
    }

    /// <summary>Serves the loopback redirect: waits for GET /callback and answers the browser.</summary>
    private static async Task<System.Collections.Specialized.NameValueCollection> WaitForCallbackAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            var target = requestLine.Split(' ') is [_, var t, ..] ? t : "";

            bool isCallback = target.StartsWith("/callback", StringComparison.Ordinal);
            var html = isCallback
                ? "<html><body style=\"font-family:Segoe UI,sans-serif;padding:3em\"><h2>Transcriber is signed in.</h2><p>You can close this tab and go back to the app.</p></body></html>"
                : "";
            var status = isCallback ? "200 OK" : "404 Not Found";
            var bytes = Encoding.UTF8.GetBytes(html);
            var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            await stream.WriteAsync(bytes, ct);

            if (isCallback)
            {
                int q = target.IndexOf('?');
                return HttpUtility.ParseQueryString(q >= 0 ? target[(q + 1)..] : "");
            }
        }
    }

    private static async Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    internal static Uri ParseServer(string serverUrl) =>
        Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http"
            ? u
            : throw new McpAuthException($"'{serverUrl}' is not a valid MCP server URL.");

    private static string Required(JsonNode node, string name) =>
        node[name]?.GetValue<string>() ?? throw new McpAuthException($"OAuth metadata is missing '{name}'.");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
