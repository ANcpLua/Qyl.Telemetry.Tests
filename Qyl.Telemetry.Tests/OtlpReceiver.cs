using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Net;
using Google.Protobuf;
using Grpc.AspNetCore.Server;
using Grpc.AspNetCore.Server.Model;
using Grpc.Core;
using Grpc.Net.Compression;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Qyl.Telemetry.SemanticConventions;
using Google.Rpc;
using BadHttpRequestException = Microsoft.AspNetCore.Http.BadHttpRequestException;
using RpcStatus = Google.Rpc.Status;

namespace Qyl.Telemetry.Tests;

[PublicAPI]
public sealed class OtlpReceiver : IAsyncDisposable
{
    internal const int DefaultMaxPayloadBytes = 16 * 1024 * 1024;
    private readonly int _maxPayloadBytes;
    private readonly Lock _gate = new();
    private readonly List<LogRecord> _logs = [];
    private readonly List<MetricRecord> _metrics = [];
    private readonly List<ExportReceipt> _receipts = [];
    private readonly List<OtlpExportRequest> _requests = [];
    private readonly List<SpanRecord> _spans = [];
    private WebApplication _application = null!;
    private TaskCompletionSource _changed = NewSignal();
    private bool _disposed;

    private OtlpReceiver(int maxPayloadBytes) => _maxPayloadBytes = maxPayloadBytes;

    public Uri Endpoint { get; private set; } = null!;
    public Uri GrpcEndpoint { get; private set; } = null!;

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _changed.TrySetResult();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _application.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static Task<OtlpReceiver> StartAsync(CancellationToken cancellationToken = default) =>
        StartAsync(DefaultMaxPayloadBytes, cancellationToken);

    /// <summary>Starts both transports with the same positive byte limit. The default overload uses 16 MiB.</summary>
    public static async Task<OtlpReceiver> StartAsync(int maxPayloadBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPayloadBytes);
        var receiver = new OtlpReceiver(maxPayloadBytes);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        ListenOptions? http = null, grpc = null;
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                http = listen;
            });
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http2;
                grpc = listen;
            });
            options.Limits.MaxRequestBodySize = maxPayloadBytes;
        });
        builder.Services.AddRequestDecompression(options =>
        {
            var gzip = options.DecompressionProviders["gzip"];
            options.DecompressionProviders.Clear();
            options.DecompressionProviders.Add("gzip", gzip);
        });
        builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = maxPayloadBytes;
            options.CompressionProviders.Clear();
            options.CompressionProviders.Add(new BoundedGrpcCompressionProvider(
                new GzipCompressionProvider(CompressionLevel.Fastest), maxPayloadBytes));
        });
        builder.Services.AddSingleton(new OtlpGrpcService(receiver));
        builder.Services.AddSingleton<IServiceMethodProvider<OtlpGrpcService>, OtlpGrpcMethodProvider>();
        receiver._application = builder.Build();
        receiver._application.UseRouting();
        receiver._application.Use(async (context, next) =>
        {
            // gRPC limits each message, not the HTTP body (which also contains frame headers).
            if (context.GetEndpoint()?.Metadata.GetMetadata<GrpcMethodMetadata>() is not null)
                context.Features.GetRequiredFeature<IHttpMaxRequestBodySizeFeature>().MaxRequestBodySize = null;
            await next(context).ConfigureAwait(false);
            // Size failures have no response message, so gRPC sends a trailers-only response.
            // Preserve the native status and detail while reporting the configured byte limit.
            if (!context.Response.HasStarted && context.Response.Headers.GrpcStatus == "8")
            {
                var detail = Uri.UnescapeDataString(context.Response.Headers.GrpcMessage.ToString());
                context.Response.Headers.GrpcMessage = Uri.EscapeDataString(
                    $"Configured {maxPayloadBytes}-byte receiver limit. {detail}");
            }
        });
        receiver._application.UseWhen(
            static context => context.GetEndpoint()?.Metadata.GetMetadata<GrpcMethodMetadata>() is null,
            static httpBranch => httpBranch.UseRequestDecompression());
        receiver._application.MapPost("/v1/{signal}", receiver.ReceiveAsync);
        receiver._application.MapGrpcService<OtlpGrpcService>();
        var transferred = false;
        try
        {
            await receiver._application.StartAsync(cancellationToken).ConfigureAwait(false);
            receiver.Endpoint = new UriBuilder("http", IPAddress.Loopback.ToString(), http!.IPEndPoint!.Port).Uri;
            receiver.GrpcEndpoint = new UriBuilder("http", IPAddress.Loopback.ToString(), grpc!.IPEndPoint!.Port).Uri;
            transferred = true;
            return receiver;
        }
        finally
        {
            if (!transferred) await receiver._application.DisposeAsync().ConfigureAwait(false);
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TelemetrySnapshot(_spans.AsReadOnlySnapshot(), _logs.AsReadOnlySnapshot(),
                _metrics.AsReadOnlySnapshot(), _receipts.AsReadOnlySnapshot())
            {
                Requests = _requests.AsReadOnlySnapshot()
            };
        }
    }

    public async Task<TelemetrySnapshot> AwaitAsync(Func<TelemetrySnapshot, bool> condition, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);
        using var deadline = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                linked.Token.ThrowIfCancellationRequested();
                Task changed;
                TelemetrySnapshot snapshot;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    snapshot = Snapshot();
                    changed = _changed.Task;
                }

                if (condition(snapshot)) return snapshot;
                await changed.WaitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException("The OTLP evidence wait was cancelled.", error, cancellationToken);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException(
                "The OTLP receiver did not observe the required evidence before the deadline.");
        }
    }

    private async Task ReceiveAsync(HttpContext context, string signal)
    {
        if (signal is not ("traces" or "logs" or "metrics"))
        {
            await FailAsync(context, signal, 404, Code.NotFound, "Unknown OTLP signal; expected traces, logs or metrics.");
            return;
        }

        if (!string.Equals(context.Request.ContentType?.Split(';')[0], "application/x-protobuf",
                StringComparison.OrdinalIgnoreCase))
        {
            await FailAsync(context, signal, 415, Code.Unimplemented, "This receiver accepts application/x-protobuf requests.");
            return;
        }

        var encoding = context.Request.Headers.ContentEncoding.ToString();
        if (encoding is not ("" or "gzip"))
        {
            await FailAsync(context, signal, 415, Code.Unimplemented, "Unsupported content encoding; expected gzip or no encoding.");
            return;
        }

        // Reject a declared oversize payload before Kestrel's first body read marks the connection
        // as a bad request. This lets the client receive the OTLP Status before uploading the body.
        if (context.Request.ContentLength > _maxPayloadBytes)
        {
            await FailAsync(context, signal, 413, Code.ResourceExhausted,
                $"The OTLP payload exceeds the {_maxPayloadBytes}-byte receiver limit.");
            return;
        }

        try
        {
            using var buffer = new MemoryStream();
            // Kestrel bounds wire bytes; request decompression applies that same limit to
            // decoded bytes, including chunked requests with no Content-Length.
            // https://learn.microsoft.com/aspnet/core/fundamentals/middleware/request-decompression#request-size-limits
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);

            var response = ProcessExport(signal, buffer.ToArray(), OtlpTransport.HttpProtobuf);
            context.Response.ContentType = "application/x-protobuf";
            await context.Response.Body.WriteAsync(response, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (BadHttpRequestException error) when (error.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await FailAsync(context, signal, 413, Code.ResourceExhausted,
                $"The OTLP payload exceeds the {_maxPayloadBytes}-byte receiver limit.");
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidDataException)
        {
            await FailAsync(context, signal, 400, Code.InvalidArgument, error.Message);
        }
    }

    internal byte[] ReceiveGrpc(string signal, byte[] payload)
    {
        try
        {
            return ProcessExport(signal, payload, OtlpTransport.Grpc);
        }
        catch (Exception error) when (error is InvalidProtocolBufferException or InvalidDataException)
        {
            Publish(new ExportReceipt(signal, 0, 0, error.Message) { Transport = OtlpTransport.Grpc });
            throw new RpcException(new Grpc.Core.Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    private byte[] ProcessExport(string signal, byte[] bytes, OtlpTransport transport)
    {
        lock (_gate)
        {
            _requests.Add(new OtlpExportRequest(signal, bytes) { Transport = transport });
        }
        var decoder = new OtlpDecode();
        long accepted = 0, rejected = 0;
        var errors = new List<string>();

        IMessage response;
        switch (signal)
        {
            case "traces":
            {
                foreach (var resource in ExportTraceServiceRequest.Parser.ParseFrom(bytes).ResourceSpans)
                foreach (var scope in resource.ScopeSpans)
                foreach (var span in scope.Spans)
                    Add(span,
                        s => decoder.Span(s, decoder.Resource(resource.Resource, resource.SchemaUrl),
                            decoder.Scope(scope.Scope, scope.SchemaUrl)), _spans);
                response = new ExportTraceServiceResponse
                {
                    PartialSuccess = rejected == 0 && decoder.Warnings.Count == 0
                        ? null
                        : new ExportTracePartialSuccess
                            { RejectedSpans = rejected, ErrorMessage = ResponseMessage(errors.Concat(decoder.Warnings)) }
                };
                break;
            }
            case "logs":
            {
                foreach (var resource in ExportLogsServiceRequest.Parser.ParseFrom(bytes).ResourceLogs)
                foreach (var scope in resource.ScopeLogs)
                foreach (var log in scope.LogRecords)
                    Add(log,
                        l => decoder.Log(l, decoder.Resource(resource.Resource, resource.SchemaUrl),
                            decoder.Scope(scope.Scope, scope.SchemaUrl)), _logs);
                response = new ExportLogsServiceResponse
                {
                    PartialSuccess = rejected == 0 && decoder.Warnings.Count == 0
                        ? null
                        : new ExportLogsPartialSuccess
                            { RejectedLogRecords = rejected, ErrorMessage = ResponseMessage(errors.Concat(decoder.Warnings)) }
                };
                break;
            }
            default:
            {
                foreach (var resource in ExportMetricsServiceRequest.Parser.ParseFrom(bytes).ResourceMetrics)
                foreach (var scope in resource.ScopeMetrics)
                foreach (var metric in scope.Metrics)
                    Add(metric,
                        m => decoder.Metric(m, decoder.Resource(resource.Resource, resource.SchemaUrl),
                            decoder.Scope(scope.Scope, scope.SchemaUrl)), _metrics, PointCount(metric));
                response = new ExportMetricsServiceResponse
                {
                    PartialSuccess = rejected == 0 && decoder.Warnings.Count == 0
                        ? null
                        : new ExportMetricsPartialSuccess
                            { RejectedDataPoints = rejected, ErrorMessage = ResponseMessage(errors.Concat(decoder.Warnings)) }
                };
                break;
            }
        }

        foreach (var warning in decoder.Warnings)
            _application.Logger.LogWarning("OTLP decoding warning: {Warning}", warning);
        Publish(new ExportReceipt(signal, accepted, rejected,
            errors.Count == 0 ? null : string.Join(" ", errors.Distinct()))
        {
            Warnings = decoder.Warnings.AsReadOnlySnapshot(), Transport = transport
        });
        return response.ToByteArray();

        void Add<TInput, TOutput>(TInput sourceRecord, Func<TInput, TOutput> decode, List<TOutput> destination,
            long units = 1)
        {
            try
            {
                var value = decode(sourceRecord);
                lock (_gate)
                {
                    destination.Add(value);
                }

                accepted += units;
            }
            catch (InvalidDataException error)
            {
                rejected += units;
                errors.Add(error.Message);
            }
        }
    }

    private async Task FailAsync(HttpContext context, string signal, int httpStatus, Code code, string message)
    {
        Publish(new ExportReceipt(signal, 0, 0, message));
        context.Response.StatusCode = httpStatus;
        var status = new RpcStatus { Code = (int)code, Message = message };
        // OTLP/HTTP requires a protobuf Status for HTTP errors and matching response encoding:
        // https://opentelemetry.io/docs/specs/otlp/#otlphttp-response
        // https://opentelemetry.io/docs/specs/otlp/#failures-1
        if (string.Equals(context.Request.ContentType?.Split(';')[0], "application/json", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonFormatter.Default.Format(status), context.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            context.Response.ContentType = "application/x-protobuf";
            await context.Response.Body.WriteAsync(status.ToByteArray(), context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static string ResponseMessage(IEnumerable<string> messages)
    {
        // Optional wire diagnostics are bounded; full diagnostics and original payloads remain in Snapshot().
        // https://opentelemetry.io/docs/specs/otlp/#otlphttp-response
        var message = string.Join(" ", messages.Distinct());
        return message.Length > 4096 ? message[..4096] : message;
    }

    private static int PointCount(Metric metric)
    {
        return metric.DataCase switch
        {
            Metric.DataOneofCase.Gauge => metric.Gauge.DataPoints.Count,
            Metric.DataOneofCase.Sum => metric.Sum.DataPoints.Count,
            Metric.DataOneofCase.Histogram => metric.Histogram.DataPoints.Count,
            Metric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram
                .DataPoints.Count,
            Metric.DataOneofCase.Summary => metric.Summary.DataPoints.Count,
            _ => 0
        };
    }

    private void Publish(ExportReceipt receipt)
    {
        lock (_gate)
        {
            _receipts.Add(receipt);
            var changed = _changed;
            _changed = NewSignal();
            changed.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class Snapshots
{
    internal static ReadOnlyCollection<T> AsReadOnlySnapshot<T>(this IEnumerable<T> values)
    {
        return new List<T>(values).AsReadOnly();
    }
}