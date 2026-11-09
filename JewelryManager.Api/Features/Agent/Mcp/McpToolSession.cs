using System.IO.Pipelines;
using System.Text.Json;
using JewelryManager.Api.Features.Agent.Model;
using JewelryManager.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JewelryManager.Api.Features.Agent.Mcp;

/// <summary>What the agent needs from the tool server: the tool list, and a way to run one tool.</summary>
public interface IToolSession : IAsyncDisposable
{
    IReadOnlyList<AgentToolSpec> Tools { get; }

    /// <summary>Runs a tool and returns its envelope (see <see cref="ToolEnvelope"/>).</summary>
    Task<string> CallAsync(string name, string argsJson, CancellationToken ct);
}

/// <summary>
/// A real MCP server and client joined by an in-memory pipe, so the agent speaks the MCP protocol to the
/// business tools without any network hop. Each session is bound to one person's API connection, and the
/// tools cannot reach anything but that connection.
/// </summary>
public sealed class McpToolSession : IToolSession
{
    private readonly McpClient _client;
    private readonly McpServer _server;
    private readonly CancellationTokenSource _stop;

    private McpToolSession(McpClient client, McpServer server, CancellationTokenSource stop, IReadOnlyList<AgentToolSpec> tools)
    {
        _client = client;
        _server = server;
        _stop = stop;
        Tools = tools;
    }

    public IReadOnlyList<AgentToolSpec> Tools { get; }

    public static async Task<McpToolSession> StartAsync(IBackendApi api, CancellationToken ct)
    {
        var toServer = new Pipe();
        var toClient = new Pipe();

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "jewelry-manager", Version = "1.0" },
            ToolCollection = [.. McpToolCatalog.Create(api)],
        };

        var server = McpServer.Create(
            new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        var stop = new CancellationTokenSource();
        _ = server.RunAsync(stop.Token);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: ct);

        // Only tools that the policy knows are offered; a new tool stays hidden until it is classified.
        var listed = await client.ListToolsAsync(cancellationToken: ct);
        var specs = listed
            .Where(t => ToolPolicy.IsKnown(t.Name))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new AgentToolSpec(t.Name, t.Description ?? "", t.JsonSchema))
            .ToList();

        return new McpToolSession(client, server, stop, specs);
    }

    public async Task<string> CallAsync(string name, string argsJson, CancellationToken ct)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? [];
        var result = await _client.CallToolAsync(name, args, cancellationToken: ct);

        var text = string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return result.IsError == true || string.IsNullOrWhiteSpace(text)
            ? ToolEnvelope.Error(string.IsNullOrWhiteSpace(text) ? "The action failed" : text)
            : text;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _stop.Dispose();
    }
}
