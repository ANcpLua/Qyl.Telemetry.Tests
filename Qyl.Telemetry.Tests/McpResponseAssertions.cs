using System.Text.Json;

namespace Qyl.Telemetry.Tests;

/// <summary>Assertions for a successful MCP tool response with a structured result.</summary>
internal static class McpResponseAssertions
{
    /// <summary>Requires success and structured content, preserving the tool's content in failure diagnostics.</summary>
    internal static JsonElement HasSuccessfulStructuredContent(this Assert _, string tool, bool? isError,
        JsonElement? structuredContent, string contentDiagnostic)
    {
        Assert.IsFalse(isError ?? false, $"{tool}: {contentDiagnostic}");
        Assert.IsNotNull(structuredContent, $"{tool} returned no structured content.");
        return structuredContent.Value;
    }
}
