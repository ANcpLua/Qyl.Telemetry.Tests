using Google.Protobuf;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using EntityRef = OpenTelemetry.Proto.Common.V1.EntityRef;
using ProtoResource = OpenTelemetry.Proto.Resource.V1.Resource;

namespace Qyl.Telemetry.Tests.Evidence;

internal static class OtlpEvidence
{
    internal static ExportTraceServiceRequest RichSpan()
    {
        var resource = new ProtoResource
        {
            DroppedAttributesCount = 3,
            Attributes =
            {
                Pair("service.name", new AnyValue { StringValue = "rich-service" }),
                Pair("service.version", new AnyValue { StringValue = "1.2.3" })
            },
            EntityRefs =
            {
                new EntityRef
                {
                    Type = "service", SchemaUrl = "entity-schema", IdKeys = { "service.name" },
                    DescriptionKeys = { "service.version" }
                }
            }
        };
        var scope = new ScopeSpans
        {
            SchemaUrl = "scope-schema",
            Scope = new InstrumentationScope
            {
                Name = "rich-scope", Version = "3.2.1", DroppedAttributesCount = 4,
                Attributes = { Pair("scope.enabled", new AnyValue { BoolValue = true }) }
            }
        };
        scope.Spans.Add(new Span
        {
            Name = "rich.operation", TraceId = Id("11111111111111111111111111111111"), SpanId = Id("2222222222222222"),
            ParentSpanId = Id("3333333333333333"),
            Kind = Span.Types.SpanKind.Consumer, Flags = 0x80000301, TraceState = "vendor=value",
            StartTimeUnixNano = 1000000001, EndTimeUnixNano = 1000000099,
            Status = new Status { Code = Status.Types.StatusCode.Error, Message = "failed exactly" },
            DroppedAttributesCount = 5, DroppedEventsCount = 6, DroppedLinksCount = 7,
            Events =
            {
                new Span.Types.Event
                {
                    Name = "exception", TimeUnixNano = 1000000002, DroppedAttributesCount = 8,
                    Attributes = { Pair("exception.message", new AnyValue { StringValue = "keep message" }) }
                },
                new Span.Types.Event { Name = "completed", TimeUnixNano = 1000000003 }
            },
            Links =
            {
                new Span.Types.Link
                {
                    TraceId = Id("44444444444444444444444444444444"), SpanId = Id("5555555555555555"),
                    TraceState = "linked=state", Flags = 0x80000301, DroppedAttributesCount = 9,
                    Attributes = { Pair("link.value", new AnyValue { IntValue = 42 }) }
                }
            }
        });
        return new ExportTraceServiceRequest
        {
            ResourceSpans =
                { new ResourceSpans { Resource = resource, SchemaUrl = "resource-schema", ScopeSpans = { scope } } }
        };
    }

    internal static KeyValue Pair(string key, AnyValue value)
    {
        return new KeyValue { Key = key, Value = value };
    }

    internal static ByteString Id(string hex)
    {
        return ByteString.CopyFrom(Convert.FromHexString(hex));
    }
}