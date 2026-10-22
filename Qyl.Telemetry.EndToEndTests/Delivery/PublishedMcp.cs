using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Qyl.Telemetry.EndToEndTests.Delivery;

/// <summary>Starts the published qyl MCP server over stdio against a Collector and builds its tool calls.</summary>
internal static class PublishedMcp
{
    internal static async Task<McpClient> ConnectAsync(string entryPoint, Uri collector, Action<string>? standardErrorLine,
        CancellationToken cancellationToken)
    {
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["QYL_COLLECTOR_URL"] = collector.ToString();
        environment["QYL_MCP_TELEMETRY"] = "0";
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "published-qyl-mcp", Command = "node", Arguments = [entryPoint, "--stdio"],
            InheritEnvironmentVariables = false, EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(10), StandardErrorLines = standardErrorLine
        });
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    internal static CallToolRequestParams ToolCall(string tool, JsonObject input) => new()
    {
        Name = tool,
        Arguments = input.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value))
    };
}
