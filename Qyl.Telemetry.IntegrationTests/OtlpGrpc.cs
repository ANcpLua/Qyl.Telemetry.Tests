using Grpc.Core;
using Grpc.Net.Client;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Qyl.Telemetry.IntegrationTests;

/// <summary>Calls a receiver's OTLP gRPC Export methods with raw bytes, so the test controls the exact wire message.</summary>
internal static class OtlpGrpc
{
    private static readonly Marshaller<byte[]> s_bytes = Marshallers.Create<byte[]>(
        static (bytes, context) => context.Complete(bytes), static context => context.PayloadAsNewBuffer());

    /// <summary>The unary Export method of the <paramref name="signal" /> service, named by its reflection descriptor.</summary>
    internal static Method<byte[], byte[]> Export(string signal)
    {
        var service = signal switch
        {
            Signals.Traces => TraceServiceReflection.Descriptor.Services[0],
            Signals.Logs => LogsServiceReflection.Descriptor.Services[0],
            _ => MetricsServiceReflection.Descriptor.Services[0]
        };
        return new Method<byte[], byte[]>(MethodType.Unary, service.FullName, "Export", s_bytes, s_bytes);
    }

    internal static AsyncUnaryCall<byte[]> ExportAsync(GrpcChannel channel, string signal, byte[] payload, CallOptions options) =>
        channel.CreateCallInvoker().AsyncUnaryCall(Export(signal), null, options, payload);
}
