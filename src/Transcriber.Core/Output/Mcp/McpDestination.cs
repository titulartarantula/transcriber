using System.Text.Json;
using System.Text.Json.Nodes;

namespace Transcriber.Core.Output.Mcp;

/// <summary>
/// Saves notes through an Obsidian MCP server's tools, so the vault is reachable from anywhere the
/// server is, with no VPN or certificate pinning.
/// </summary>
public sealed class McpDestination(string serverUrl, string folder) : INoteDestination
{
    internal const string CreateTool = "obsidian_create_note";
    internal const string ListTool = "obsidian_list_directory";

    private readonly string _folder = folder.Trim().Trim('/', '\\').Replace('\\', '/');

    public async Task<string> TestAsync(CancellationToken ct = default)
    {
        using var client = await ConnectAsync(ct);
        var tools = await client.ListToolsAsync(ct);
        if (!tools.Contains(CreateTool))
            throw new McpException($"The MCP server doesn't offer a '{CreateTool}' tool, so notes can't be saved through it.");

        var existing = await ListFolderAsync(client, ct);
        var where = _folder.Length == 0 ? "the vault root" : $"'{_folder}'";
        var name = client.ServerName is { Length: > 0 } n ? n : "the MCP server";
        return existing is null
            ? $"Connected to {name}. {where} doesn't exist yet; it will be created with the first note."
            : $"Connected to {name}. {where} has {existing.Count} items.";
    }

    public async Task<SavedNote> SaveAsync(string fileName, string markdown, CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        // obsidian_create_note overwrites, so pick a free name from the folder listing first.
        var existing = await ListFolderAsync(client, ct) ?? [];
        var unique = await FileNames.UniqueAsync(fileName, n => Task.FromResult(existing.Contains(n)));
        var path = _folder.Length == 0 ? unique : $"{_folder}/{unique}";

        await client.CallToolAsync(CreateTool, new JsonObject { ["path"] = path, ["content"] = markdown }, ct);
        return new SavedNote($"Obsidian vault (via MCP): {path}", null);
    }

    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        var creds = McpCredentialStore.Load(serverUrl)
            ?? throw new McpAuthException("Not signed in to the Obsidian MCP server. Sign in from Settings → Output.");
        var client = new McpClient(serverUrl, creds);
        try
        {
            await client.InitializeAsync(ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Names in the target folder, or null if the folder doesn't exist.</summary>
    private async Task<HashSet<string>?> ListFolderAsync(McpClient client, CancellationToken ct)
    {
        var args = new JsonObject { ["path"] = _folder.Length == 0 ? null : _folder };
        string text;
        try
        {
            text = await client.CallToolAsync(ListTool, args, ct);
        }
        catch (McpToolError e) when (e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return ParseListing(text);
    }

    /// <summary>The tool returns names one per line, sometimes wrapped as {"result": "..."}.</summary>
    internal static HashSet<string> ParseListing(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject { } o && o["result"] is JsonValue v && v.TryGetValue<string>(out var inner))
                text = inner;
        }
        catch (JsonException)
        {
        }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => !n.EndsWith('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
