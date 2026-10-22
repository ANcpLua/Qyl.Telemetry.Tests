using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Qyl.Telemetry.Tests;

/// <summary>
///     Adds an OpenTelemetry SDK OTLP exporter that sends gzip-compressed records one at a time to a test receiver: the
///     signal's <c>v1/{signal}</c> route over OTLP/HTTP protobuf, or the receiver address itself over gRPC.
/// </summary>
[JetBrains.Annotations.PublicAPI]
public static class OtlpCaptureExporter
{
    extension(TracerProviderBuilder builder)
    {
        /// <param name="receiver">The receiver's HTTP endpoint, or its gRPC endpoint for <see cref="OtlpTransport.Grpc" />.</param>
        /// <param name="transport">The OTLP transport the exporter uses.</param>
        /// <param name="configure">Further exporter settings, applied last.</param>
        public TracerProviderBuilder AddOtlpCapture(Uri receiver, OtlpTransport transport = OtlpTransport.HttpProtobuf,
            Action<OtlpExporterOptions>? configure = null) =>
            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = Endpoint(receiver, "traces", transport);
                Capture(options, transport);
                configure?.Invoke(options);
            });
    }

    extension(MeterProviderBuilder builder)
    {
        /// <param name="receiver">The receiver's HTTP endpoint, or its gRPC endpoint for <see cref="OtlpTransport.Grpc" />.</param>
        /// <param name="transport">The OTLP transport the exporter uses.</param>
        public MeterProviderBuilder AddOtlpCapture(Uri receiver, OtlpTransport transport = OtlpTransport.HttpProtobuf) =>
            builder.AddOtlpExporter(options =>
            {
                options.Endpoint = Endpoint(receiver, "metrics", transport);
                Capture(options, transport);
            });
    }

    extension(OpenTelemetryLoggerOptions logging)
    {
        /// <param name="receiver">The receiver's HTTP endpoint, or its gRPC endpoint for <see cref="OtlpTransport.Grpc" />.</param>
        /// <param name="transport">The OTLP transport the exporter uses.</param>
        public OpenTelemetryLoggerOptions AddOtlpCapture(Uri receiver, OtlpTransport transport = OtlpTransport.HttpProtobuf) =>
            logging.AddOtlpExporter(options =>
            {
                options.Endpoint = Endpoint(receiver, "logs", transport);
                Capture(options, transport);
            });
    }

    private static Uri Endpoint(Uri receiver, string signal, OtlpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        return transport == OtlpTransport.Grpc ? receiver : new Uri(receiver, "v1/" + signal);
    }

    private static void Capture(OtlpExporterOptions options, OtlpTransport transport)
    {
        options.Protocol = transport == OtlpTransport.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
        options.Compression = OtlpExportCompression.GZip;
        options.ExportProcessorType = ExportProcessorType.Simple;
    }
}
