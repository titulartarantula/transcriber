using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transcriber.Core.Output.Mcp;

public sealed class McpException(string message) : Exception(message);

/// <summary>
/// Just enough of an MCP client (Streamable HTTP transport) to initialize a session and call tools.
/// Responses may arrive as plain JSON or as a server-sent event stream; both are handled.
/// </summary>
public sealed class McpClient : IDisposable
{
    private const string ProtocolVersion = "2025-06-18";

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly Uri _endpoint;
    private readonly McpCredentials _creds;
    private string? _sessionId;
    private string? _negotiatedVersion;
    private int _nextId;

    public McpClient(string serverUrl, McpCredentials creds)
    {
        _endpoint = McpAuth.ParseServer(serverUrl);
        _creds = creds;
    }

    public string? ServerName { get; private set; }

    public async Task InitializeAsync(CancellationToken ct)
    {
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "Transcriber", ["version"] = "0.1.0" },
        }, ct);
        _negotiatedVersion = result["protocolVersion"]?.GetValue<string>() ?? ProtocolVersion;
        ServerName = result["serverInfo"]?["name"]?.GetValue<string>();
        await NotifyAsync("notifications/initialized", ct);
    }

    public async Task<IReadOnlyList<string>> ListToolsAsync(CancellationToken ct)
    {
        var result = await RequestAsync("tools/list", new JsonObject(), ct);
        return result["tools"]?.AsArray().Select(t => t?["name"]?.GetValue<string>() ?? "").ToList() ?? [];
    }

    /// <summary>Calls a tool and returns its text output. A tool-level error comes back as <see cref="McpToolError"/>.</summary>
    public async Task<string> CallToolAsync(string name, JsonObject arguments, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, ct);
        var text = string.Join("\n", result["content"]?.AsArray()
            .Where(c => c?["type"]?.GetValue<string>() == "text")
            .Select(c => c!["text"]!.GetValue<string>()) ?? []);
        if (result["isError"]?.GetValue<bool>() == true) throw new McpToolError(name, text);
        return text;
    }

    private async Task<JsonNode> RequestAsync(string method, JsonObject @params, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params };
        using var response = await SendAsync(message, ct);

        var reply = await ReadReplyAsync(response, id, ct)
            ?? throw new McpException($"The MCP server sent no reply to {method}.");
        if (reply["error"] is { } error)
            throw new McpException($"MCP {method} failed: {error["message"]?.GetValue<string>() ?? error.ToJsonString()}");
        return reply["result"] ?? new JsonObject();
    }

    private async Task NotifyAsync(string method, CancellationToken ct)
    {
        using var _ = await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method }, ct);
    }

    /// <summary>POSTs a JSON-RPC message, refreshing the access token once if it's expired or rejected.</summary>
    private async Task<HttpResponseMessage> SendAsync(JsonObject message, CancellationToken ct)
    {
        if (_creds.NeedsRefresh) await McpAuth.RefreshAsync(_creds, ct);

        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _creds.AccessToken);
            if (_sessionId is not null) request.Headers.Add("Mcp-Session-Id", _sessionId);
            if (_negotiatedVersion is not null) request.Headers.Add("MCP-Protocol-Version", _negotiatedVersion);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException e)
            {
                throw new McpException($"Could not reach the MCP server at {_endpoint.Host}: {e.Message}");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                response.Dispose();
                await McpAuth.RefreshAsync(_creds, ct);
                continue;
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new McpAuthException("The MCP server rejected the sign-in. Sign in again in Settings.");
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                response.Dispose();
                throw new McpException($"MCP server returned {(int)response.StatusCode}: {Truncate(body)}");
            }

            if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.First();
            return response;
        }
    }

    /// <summary>Finds the JSON-RPC response with <paramref name="id"/> in a JSON body or an SSE stream.</summary>
    internal static async Task<JsonNode?> ReadReplyAsync(HttpResponseMessage response, int id, CancellationToken ct)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType == "text/event-stream")
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data.Append(line.AsSpan(5).TrimStart(' ')).Append('\n');
                    continue;
                }
                if (line.Length > 0 || data.Length == 0) continue;

                // Blank line ends an event; servers may interleave notifications before our reply.
                if (Matches(Parse(data.ToString()), id) is { } reply) return reply;
                data.Clear();
            }
            return data.Length > 0 ? Matches(Parse(data.ToString()), id) : null;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        return body.Length == 0 ? null : Matches(Parse(body), id);
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); }
        catch (JsonException) { return null; }
    }

    private static JsonNode? Matches(JsonNode? node, int id) =>
        node is JsonObject o && o["id"] is JsonValue v && v.TryGetValue<int>(out var got) && got == id ? node : null;

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "…";

    public void Dispose() => _http.Dispose();
}

public sealed class McpToolError(string tool, string message) : Exception($"{tool}: {message}")
{
    public string Tool { get; } = tool;
}
