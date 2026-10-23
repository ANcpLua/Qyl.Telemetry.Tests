using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>Where an analysis question is asked: the Collector's HTTP route or the matching source MCP tool.</summary>
public enum AnalysisPath
{
    Http,
    Mcp
}

internal static class AnalysisTools
{
    internal const string Triage = "triage";
    internal const string FailureContext = "get_failure_context";
    internal const string VerifyFix = "verify_fix";
    internal const string CompareGroups = "compare_groups";
    internal const string QueryMetric = "query_metric";
    internal const string GetTrace = "get_trace";
}

/// <summary>Owns the locally staged Collector and the MCP server from the same source checkout.</summary>
internal sealed class AnalysisSession : IAsyncDisposable
{
    private readonly string _mcpEntryPoint;
    private CollectorFixture _collector;
    private McpClient _mcp;

    private AnalysisSession(string mcpEntryPoint, CollectorFixture collector, McpClient mcp)
    {
        _mcpEntryPoint = mcpEntryPoint;
        _collector = collector;
        _mcp = mcp;
    }

    internal CollectorClient Client => _collector.Client;

    /// <summary>The clock the seeded incidents and their windows are placed on.</summary>
    internal TimeProvider TimeProvider => _collector.TimeProvider;

    internal static async Task<AnalysisSession> StartAsync(TestContext context)
    {
        var entryPoint = (await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "local-mcp-entrypoint.txt"),
            context.CancellationToken)).Trim();
        if (!File.Exists(entryPoint))
            throw new FileNotFoundException("The analysis suite requires the parent qyl MCP source checkout.", entryPoint);
        var collector = await CollectorFixture.StartAsync(CollectorFixture.DefaultAssembly,
            timeProvider: TimeProvider.System, cancellationToken: context.CancellationToken);
        try
        {
            return new AnalysisSession(entryPoint, collector, await ConnectAsync(entryPoint, collector, context.CancellationToken));
        }
        catch
        {
            await collector.DisposeAsync();
            throw;
        }
    }

    /// <summary>Asks one analysis question and reads the answer as its contract type.</summary>
    internal async Task<T> AskAsync<T>(AnalysisPath path, string tool, JsonObject input, CancellationToken cancellationToken)
    {
        if (path == AnalysisPath.Http) return await Client.AnalyzeAsync<T>(tool, input, cancellationToken);
        var result = await _mcp.CallToolAsync(McpProcess.ToolCall(tool, input), cancellationToken);
        Assert.IsFalse(result.IsError ?? false, $"{tool}: {JsonSerializer.Serialize(result.Content)}");
        Assert.IsNotNull(result.StructuredContent, $"{tool} returned no structured content.");
        return result.StructuredContent.Value.Deserialize<T>(JsonSerializerOptions.Web)
            ?? throw new InvalidDataException($"{tool} returned empty structured content.");
    }

    /// <summary>Asks one analysis question that must fail and returns the HTTP status (null on MCP) with the error as text.</summary>
    internal async Task<(int? Status, string Error)> AskErrorAsync(AnalysisPath path, string tool, JsonObject input, CancellationToken cancellationToken)
    {
        if (path == AnalysisPath.Http)
        {
            try
            {
                await Client.AnalyzeAsync<JsonElement>(tool, input, cancellationToken);
            }
            catch (HttpRequestException rejected)
            {
                return ((int?)rejected.StatusCode, rejected.Message);
            }
            Assert.Fail($"{tool} succeeded over HTTP but was expected to fail.");
        }
        var result = await _mcp.CallToolAsync(McpProcess.ToolCall(tool, input), cancellationToken);
        Assert.IsTrue(result.IsError ?? false, $"{tool} succeeded over MCP but was expected to fail: {JsonSerializer.Serialize(result.StructuredContent)}");
        return (null, JsonSerializer.Serialize(result.StructuredContent) + JsonSerializer.Serialize(result.Content));
    }

    /// <summary>Restarts the Collector on its data directory and reconnects the MCP server to it.</summary>
    internal async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _mcp.DisposeAsync();
        _collector = await _collector.RestartAsync(cancellationToken);
        _mcp = await ConnectAsync(_mcpEntryPoint, _collector, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _mcp.DisposeAsync(); }
        finally { await _collector.DisposeAsync(); }
    }

    private static Task<McpClient> ConnectAsync(string entryPoint, CollectorFixture collector, CancellationToken cancellationToken) =>
        McpProcess.ConnectAsync("bun", entryPoint, collector.Address, null, cancellationToken);
}
