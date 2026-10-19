using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.SemanticConventions.Incubating.Views;
using Qyl.Telemetry.SemanticConventions.Incubating.Spans;
using Qyl.Telemetry.SemanticConventions.Spans;

using var input = JsonDocument.Parse(args[0]);
var value = input.RootElement;
object? typed = value.ValueKind switch
{
    JsonValueKind.String => value.GetString(),
    JsonValueKind.Number => value.GetInt64(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.Null => null,
    JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array =>
        throw new ArgumentException("The probe expects a scalar JSON value."),
    _ => throw new InvalidDataException($"Unknown JSON value kind '{value.ValueKind}'.")
};
var record = new SpanRecord("00112233445566778899aabbccddeeff", "0011223344556677", "tool")
{
    Kind = SpanKind.Internal,
    Attributes = new Dictionary<string, object?>
    {
        ["gen_ai.operation.name"] = "execute_tool", ["gen_ai.tool.name"] = typed
    }
};
var created = GenAiExecuteToolSpan.TryCreate(record, out var view);
var method = HttpSpanDefinitions.HttpServer.Attributes.Single(attribute => attribute.Name == "http.request.method");
var status = HttpSpanDefinitions.HttpServer.Attributes.Single(attribute => attribute.Name == "http.response.status_code");
var plan = GenAiIncubatingSpanDefinitions.GenAiPlanInternal;
var association = (EntityRef)plan.EntityAssociations.Single();
using var stream = new MemoryStream();
using (var writer = new Utf8JsonWriter(stream))
{
    writer.WriteStartObject();
    writer.WriteBoolean("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
    writer.WriteBoolean("created", created);
    writer.WriteBoolean("hasView", view is not null);
    if (view is not null) writer.WriteString("toolName", view.Value.ToolName);
    writer.WriteString("methodPhase", method.Phase.ToString());
    writer.WriteBoolean("methodSamplingRelevant", method.SamplingRelevant);
    writer.WriteString("statusPhase", status.Phase.ToString());
    writer.WriteBoolean("statusSamplingRelevant", status.SamplingRelevant);
    writer.WriteString("signalRequirement", plan.RequirementLevel.ToString());
    writer.WriteString("entityType", association.Type);
    writer.WriteString("registrySchema", association.Provenance.SchemaUrl);
    writer.WriteEndObject();
}
Console.WriteLine(Encoding.UTF8.GetString(stream.ToArray()));
