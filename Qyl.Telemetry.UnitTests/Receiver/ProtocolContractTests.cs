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
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_ExportService_HasTheOfficialFullName(string signal)
    {
        var service = Assert.ContainsSingle(Descriptor(signal).Services);
        Assert.AreEqual($"opentelemetry.proto.collector.{signal}.v1.{Service(signal)}Service", service.FullName);
    }

    [TestMethod]
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_ExportService_HasOnlyTheExportMethod(string signal)
    {
        Assert.AreEqual("Export", Assert.ContainsSingle(ExportService(signal).Methods).Name);
    }

    [TestMethod]
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_ExportMethod_IsNotClientStreaming(string signal)
    {
        Assert.IsFalse(Export(signal).IsClientStreaming);
    }

    [TestMethod]
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_ExportMethod_IsNotServerStreaming(string signal)
    {
        Assert.IsFalse(Export(signal).IsServerStreaming);
    }

    [TestMethod]
    [DataRow(Trace, ResourceSpans)]
    [DataRow(Logs, ResourceLogs)]
    [DataRow(Metrics, ResourceMetrics)]
    public void GeneratedProtocol_ExportRequestResourceField_IsFieldOne(string signal, string resourceField)
    {
        var resource = Export(signal).InputType.FindFieldByName(resourceField);
        Assert.IsNotNull(resource);
        Assert.AreEqual(1, resource.FieldNumber);
    }

    [TestMethod]
    [DataRow(Trace, ResourceSpans)]
    [DataRow(Logs, ResourceLogs)]
    [DataRow(Metrics, ResourceMetrics)]
    public void GeneratedProtocol_ExportRequestResourceField_IsRepeated(string signal, string resourceField)
    {
        var resource = Export(signal).InputType.FindFieldByName(resourceField);
        Assert.IsNotNull(resource);
        Assert.IsTrue(resource.IsRepeated);
    }

    [TestMethod]
    [DataRow(Trace, ResourceSpans)]
    [DataRow(Logs, ResourceLogs)]
    [DataRow(Metrics, ResourceMetrics)]
    public void GeneratedProtocol_ExportRequestResourceField_IsAMessage(string signal, string resourceField)
    {
        var resource = Export(signal).InputType.FindFieldByName(resourceField);
        Assert.IsNotNull(resource);
        Assert.AreEqual(FieldType.Message, resource.FieldType);
    }

    [TestMethod]
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_ExportResponsePartialSuccess_IsFieldOne(string signal)
    {
        var partial = Export(signal).OutputType.FindFieldByName("partial_success");
        Assert.IsNotNull(partial);
        Assert.AreEqual(1, partial.FieldNumber);
    }

    [TestMethod]
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_ExportRequestType_IsInternal(string signal)
    {
        Assert.IsFalse(Export(signal).InputType.ClrType.IsPublic);
    }

    [TestMethod]
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_ExportResponseType_IsInternal(string signal)
    {
        Assert.IsFalse(Export(signal).OutputType.ClrType.IsPublic);
    }

    [TestMethod]
    [DataRow(Trace)]
    [DataRow(Logs)]
    [DataRow(Metrics)]
    public void GeneratedProtocol_DescriptorFile_HasTheOfficialPath(string signal)
    {
        Assert.AreEqual($"opentelemetry/proto/collector/{signal}/v1/{signal}_service.proto", Descriptor(signal).Name);
    }

    private static ServiceDescriptor ExportService(string signal) => Descriptor(signal).Services.Single();

    private static MethodDescriptor Export(string signal) => ExportService(signal).Methods.Single();

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