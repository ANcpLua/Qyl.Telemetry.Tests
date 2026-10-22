using Google.Protobuf.Reflection;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class ProtocolContractTests
{
    private const string Trace = "trace";
    private const string Logs = "logs";
    private const string Metrics = "metrics";
    private const string ResourceSpans = "resource_spans";
    private const string ResourceLogs = "resource_logs";
    private const string ResourceMetrics = "resource_metrics";

    [TestMethod]
    [DataRow(Trace, ResourceSpans)]
    [DataRow(Logs, ResourceLogs)]
    [DataRow(Metrics, ResourceMetrics)]
    public void Internal_generated_protocol_matches_the_official_export_service_contract(string signal,
        string resourceField)
    {
        var descriptor = Descriptor(signal);
        var service = Assert.ContainsSingle(descriptor.Services);
        Assert.AreEqual($"opentelemetry.proto.collector.{signal}.v1.{Service(signal)}Service", service.FullName);
        var export = Assert.ContainsSingle(service.Methods);
        Assert.AreEqual("Export", export.Name);
        Assert.IsFalse(export.IsClientStreaming);
        Assert.IsFalse(export.IsServerStreaming);
        var resource = export.InputType.FindFieldByName(resourceField);
        Assert.IsNotNull(resource);
        Assert.AreEqual(1, resource.FieldNumber);
        Assert.IsTrue(resource.IsRepeated);
        Assert.AreEqual(FieldType.Message, resource.FieldType);
        var partial = export.OutputType.FindFieldByName("partial_success");
        Assert.IsNotNull(partial);
        Assert.AreEqual(1, partial.FieldNumber);
        Assert.IsFalse(export.InputType.ClrType.IsPublic);
        Assert.IsFalse(export.OutputType.ClrType.IsPublic);
        Assert.AreEqual($"opentelemetry/proto/collector/{signal}/v1/{signal}_service.proto", descriptor.Name);
    }

    private static FileDescriptor Descriptor(string signal)
    {
        return signal switch
        {
            Trace => TraceServiceReflection.Descriptor, Logs => LogsServiceReflection.Descriptor,
            Metrics => MetricsServiceReflection.Descriptor,
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        };
    }

    private static string Service(string signal)
    {
        return signal switch
        {
            Trace => "Trace", Logs => "Logs", Metrics => "Metrics",
            _ => throw new ArgumentOutOfRangeException(nameof(signal))
        };
    }
}