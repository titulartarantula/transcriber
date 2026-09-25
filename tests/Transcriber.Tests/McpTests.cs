using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Transcriber.Core.Output.Mcp;
using Xunit;

namespace Transcriber.Tests;

public class McpListingTests
{
    [Fact]
    public void Parses_wrapped_listing_and_skips_folders() =>
        Assert.Equal(new[] { "a.md", "b c.md" },
            McpDestination.ParseListing("{\"result\":\"a.md\\nsub/\\nb c.md\"}").Order());

    [Fact]
    public void Parses_plain_listing() =>
        Assert.Equal(new[] { "x.md" }, McpDestination.ParseListing("x.md\n\nfolder/\n"));
}

/// <summary>
/// Drives McpDestination against an in-process fake MCP server: session headers, SSE replies with
/// interleaved notifications, token refresh after a 401, and no-overwrite naming.
/// </summary>
[Collection("McpCredentialStore")]
public sealed class McpClientTests : IDisposable
{
    private readonly string _credFile = Path.Combine(Path.GetTempPath(), "transcriber-mcp-" + Guid.NewGuid() + ".dat");
    private readonly FakeMcpServer _server = new();

    public McpClientTests() => McpCredentialStore.PathOverride = _credFile;

    [Fact]
    public async Task Saves_note_with_unique_name_after_refreshing_a_stale_token()
    {
        McpCredentialStore.Save(new McpCredentials
        {
            ServerUrl = _server.McpUrl,
            Resource = _server.McpUrl,
            TokenEndpoint = _server.BaseUrl + "token",
            ClientId = "client-1",
            AccessToken = "stale",
            RefreshToken = "refresh-1",
        });

        var dest = new McpDestination(_server.McpUrl, "Transcripts");
        var saved = await dest.SaveAsync("a.md", "# hello", default);

        Assert.Equal("Obsidian vault (via MCP): Transcripts/a (2).md", saved.Description);
        Assert.Equal(("Transcripts/a (2).md", "# hello"), _server.Created.Single());
        Assert.True(_server.Refreshed);
        Assert.True(_server.SessionHeaderSeenAfterInit);
        Assert.Equal("fresh", McpCredentialStore.Load(_server.McpUrl)!.AccessToken);
    }

    [Fact]
    public async Task Test_reports_missing_folder_and_server_name()
    {
        McpCredentialStore.Save(new McpCredentials
        {
            ServerUrl = _server.McpUrl, Resource = _server.McpUrl, TokenEndpoint = _server.BaseUrl + "token",
            ClientId = "c", AccessToken = "fresh",
        });

        var message = await new McpDestination(_server.McpUrl, "Missing").TestAsync();

        Assert.Contains("fake-obsidian", message);
        Assert.Contains("doesn't exist yet", message);
    }

    [Fact]
    public async Task Not_signed_in_is_a_clear_error()
    {
        var e = await Assert.ThrowsAsync<McpAuthException>(() => new McpDestination(_server.McpUrl, "").SaveAsync("n.md", "x", default));
        Assert.Contains("Sign in", e.Message);
    }

    public void Dispose()
    {
        McpCredentialStore.PathOverride = null;
        _server.Dispose();
        if (File.Exists(_credFile)) File.Delete(_credFile);
    }

    private sealed class FakeMcpServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public FakeMcpServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://localhost:{port}/";
            _listener.Prefixes.Add(BaseUrl);
            _listener.Start();
            _loop = Task.Run(Loop);
        }

        public string BaseUrl { get; }
        public string McpUrl => BaseUrl + "mcp";
        public List<(string Path, string Content)> Created { get; } = new();
        public bool Refreshed { get; private set; }
        public bool SessionHeaderSeenAfterInit { get; private set; }

        private async Task Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                try { await Handle(ctx); }
                finally { ctx.Response.Close(); }
            }
        }

        private async Task Handle(HttpListenerContext ctx)
        {
            var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();

            if (ctx.Request.Url!.AbsolutePath == "/token")
            {
                var form = System.Web.HttpUtility.ParseQueryString(body);
                if (form["grant_type"] == "refresh_token" && form["refresh_token"] == "refresh-1")
                {
                    Refreshed = true;
                    await Write(ctx, "application/json", "{\"access_token\":\"fresh\",\"expires_in\":3600}");
                }
                else
                {
                    ctx.Response.StatusCode = 400;
                    await Write(ctx, "application/json", "{\"error\":\"invalid_grant\"}");
                }
                return;
            }

            if (ctx.Request.Headers["Authorization"] != "Bearer fresh")
            {
                ctx.Response.StatusCode = 401;
                return;
            }

            var msg = JsonNode.Parse(body)!;
            var method = msg["method"]!.GetValue<string>();
            if (method != "initialize" && ctx.Request.Headers["Mcp-Session-Id"] == "session-1") SessionHeaderSeenAfterInit = true;

            switch (method)
            {
                case "initialize":
                    ctx.Response.AddHeader("Mcp-Session-Id", "session-1");
                    await Reply(ctx, msg, new JsonObject { ["protocolVersion"] = "2025-06-18", ["serverInfo"] = new JsonObject { ["name"] = "fake-obsidian" } });
                    break;
                case "notifications/initialized":
                    ctx.Response.StatusCode = 202;
                    break;
                case "tools/list":
                    // As an event stream, with a notification ahead of the actual reply.
                    var reply = new JsonObject
                    {
                        ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.GetValue<int>(),
                        ["result"] = new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "obsidian_create_note" }, new JsonObject { ["name"] = "obsidian_list_directory" }) },
                    };
                    await Write(ctx, "text/event-stream",
                        "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\n\n" +
                        $"event: message\ndata: {reply.ToJsonString()}\n\n");
                    break;
                case "tools/call":
                    var name = msg["params"]!["name"]!.GetValue<string>();
                    var args = msg["params"]!["arguments"]!;
                    if (name == "obsidian_list_directory" && args["path"]?.GetValue<string>() == "Missing")
                        await Reply(ctx, msg, Text("not found: /vault/Missing/ — Not Found", isError: true));
                    else if (name == "obsidian_list_directory")
                        await Reply(ctx, msg, Text("{\"result\":\"a.md\\nsub/\"}"));
                    else
                    {
                        Created.Add((args["path"]!.GetValue<string>(), args["content"]!.GetValue<string>()));
                        await Reply(ctx, msg, Text("Created"));
                    }
                    break;
            }
        }

        private static JsonObject Text(string text, bool isError = false) => new()
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };

        private static Task Reply(HttpListenerContext ctx, JsonNode request, JsonNode result) =>
            Write(ctx, "application/json", new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.GetValue<int>(), ["result"] = result }.ToJsonString());

        private static async Task Write(HttpListenerContext ctx, string contentType, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }
}
