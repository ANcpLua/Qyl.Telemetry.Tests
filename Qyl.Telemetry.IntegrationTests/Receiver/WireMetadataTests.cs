using static Qyl.Telemetry.Tests.Evidence.OtlpEvidence;
using System.Diagnostics;
using Google.Protobuf;
using JetBrains.Annotations;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;
using Qyl.Telemetry.SemanticConventions;
using EntityRef = OpenTelemetry.Proto.Common.V1.EntityRef;
using ProtoResource = OpenTelemetry.Proto.Resource.V1.Resource;

namespace Qyl.Telemetry.IntegrationTests.Receiver;

[TestClass]
public sealed class WireMetadataTests
{
    [UsedImplicitly] public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Complete_span_resource_scope_events_and_links_survive_in_wire_order()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = RichSpan();
        using var response =
            await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(), TestContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var span = Assert.ContainsSingle(receiver.Snapshot().Spans);
        Assert.AreEqual("11111111111111111111111111111111", span.TraceId);
        Assert.AreEqual("2222222222222222", span.SpanId);
        Assert.AreEqual("3333333333333333", span.ParentSpanId);
        Assert.AreEqual("rich.operation", span.Name);
        Assert.AreEqual(SpanKind.Consumer, span.Kind);
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.AreEqual("failed exactly", span.StatusDescription);
        Assert.AreEqual("vendor=value", span.TraceState);
        Assert.AreEqual(0x80000301u, span.Flags);
        Assert.AreEqual(1000000001UL, span.StartTimeUnixNano);
        Assert.AreEqual(1000000099UL, span.EndTimeUnixNano);
        Assert.AreEqual("resource-schema", span.Resource.SchemaUrl);
        Assert.AreEqual(3u, span.Resource.DroppedAttributesCount);
        var entity = Assert.ContainsSingle(span.Resource.EntityReferences);
        Assert.AreEqual("service", entity.Type);
        Assert.AreSequenceEqual(["service.name"], entity.IdentityKeys);
        Assert.AreSequenceEqual(["service.version"], entity.DescriptionKeys);
        Assert.AreEqual("entity-schema", entity.SchemaUrl);
        Assert.AreEqual("rich-service", span.Service);
        Assert.AreEqual("rich-scope", span.Scope?.Name);
        Assert.AreEqual("3.2.1", span.Scope?.Version);
        Assert.AreEqual("scope-schema", span.Scope?.SchemaUrl);
        Assert.AreEqual(4u, span.Scope?.DroppedAttributesCount);
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(span.Scope?.Attributes["scope.enabled"]));
        Assert.AreEqual(5u, span.DroppedAttributesCount);
        Assert.AreEqual(6u, span.DroppedEventsCount);
        Assert.AreEqual(7u, span.DroppedLinksCount);
        Assert.AreSequenceEqual(["exception", "completed"], span.Events.Select(e => e.Name));
        Assert.AreEqual(1000000002UL, span.Events[0].TimeUnixNano);
        Assert.AreEqual("keep message", span.Events[0].Attributes["exception.message"]);
        Assert.AreEqual(8u, span.Events[0].DroppedAttributesCount);
        var link = Assert.ContainsSingle(span.Links);
        Assert.AreEqual("44444444444444444444444444444444", link.TraceId);
        Assert.AreEqual("5555555555555555", link.SpanId);
        Assert.AreEqual("linked=state", link.TraceState);
        Assert.AreEqual(0x80000301u, link.Flags);
        Assert.AreEqual(9u, link.DroppedAttributesCount);
        Assert.AreEqual(42L, link.Attributes["link.value"]);
    }

    [TestMethod]
    public async Task Recursive_otlp_values_keep_their_types_without_stringification()
    {
        await using var receiver = await OtlpReceiver.StartAsync(TestContext.CancellationToken);
        var request = RichSpan();
        var input = request.ResourceSpans[0].ScopeSpans[0].Spans[0];
        input.Attributes.Add(Pair("bytes", new AnyValue { BytesValue = ByteString.CopyFrom([0, 127, 255]) }));
        input.Attributes.Add(Pair("array",
            new AnyValue
            {
                ArrayValue = new ArrayValue
                {
                    Values =
                    {
                        new AnyValue { IntValue = -7 }, new AnyValue { BoolValue = true },
                        new AnyValue { DoubleValue = 0.125 }, new AnyValue()
                    }
                }
            }));
        input.Attributes.Add(Pair("map",
            new AnyValue
            {
                KvlistValue = new KeyValueList { Values = { Pair("nested", new AnyValue { StringValue = "exact" }) } }
            }));
        using var response =
            await ReceiverTests.PostAsync(receiver.Endpoint, request.ToByteArray(), TestContext.CancellationToken);
        response.EnsureSuccessStatusCode();
        var span = Assert.ContainsSingle(receiver.Snapshot().Spans);
        Assert.AreEqual("rich.operation", span.Name);
        Assert.AreEqual("11111111111111111111111111111111", span.TraceId);
        Assert.AreSequenceEqual(new byte[] { 0, 127, 255 }, Assert.IsInstanceOfType<byte[]>(span.Attributes["bytes"]));
        var array = Assert.IsInstanceOfType<IReadOnlyList<object?>>(span.Attributes["array"]);
        Assert.AreSequenceEqual([-7L, true, 0.125, null], array);
        var map = Assert.IsInstanceOfType<IReadOnlyDictionary<string, object?>>(span.Attributes["map"]);
        Assert.AreEqual("exact", map["nested"]);
    }

}
