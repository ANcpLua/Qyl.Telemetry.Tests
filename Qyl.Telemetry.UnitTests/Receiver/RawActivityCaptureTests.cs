using System.Diagnostics;
using System.Text.Json;
using Qyl.Telemetry.SemanticConventions.Attributes.Url;
using Qyl.Telemetry.SemanticConventions.Incubating.Attributes.Grpc;
using Qyl.Telemetry.Tests.Capture;

namespace Qyl.Telemetry.UnitTests.Receiver;

[TestClass]
public sealed class RawActivityCaptureTests
{
    private const string Key = "http.request.header.x-example";

    [TestMethod]
    public void RawActivityCapture_Snapshot_OwnsTheOriginalArrayAndOperationPath()
    {
        string[] values = ["first", "first", "", "comma,kept"];
        using var activity = new Activity("http").SetTag(UrlAttributes.Full, "https://example.invalid/ok?ignored=true")
            .SetTag(Key, values).Start();
        var snapshot = ActivitySnapshot.Capture(activity);
        values[0] = "mutated";
        string[] replacement = ["replaced"];
        activity.SetTag(Key, replacement);

        Assert.AreEqual("/ok", snapshot.Path);
        CapturedActivityAssertions.RequireStringValues(snapshot.Tags, Key, "first", "first", "", "comma,kept");
    }

    [TestMethod]
    [DataRow(RawViolation.Missing)]
    [DataRow(RawViolation.Null)]
    [DataRow(RawViolation.Scalar)]
    [DataRow(RawViolation.ObjectArray)]
    [DataRow(RawViolation.List)]
    [DataRow(RawViolation.Reordered)]
    [DataRow(RawViolation.DuplicateLost)]
    public void RawActivityCapture_WrongClrTypeOrChangedSequence_FailsExactValues(RawViolation violation)
    {
        var tags = new Dictionary<string, object?>();
        if (violation != RawViolation.Missing)
        {
            tags[Key] = violation switch
            {
                RawViolation.Missing or RawViolation.Null => null,
                RawViolation.Scalar => "first",
                RawViolation.ObjectArray => new object[] { "first", "first", "" },
                RawViolation.List => new List<string> { "first", "first", "" },
                RawViolation.Reordered => new[] { "", "first", "first" },
                RawViolation.DuplicateLost => new[] { "first", "" },
                _ => throw new ArgumentOutOfRangeException(nameof(violation))
            };
        }

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireStringValues(tags, Key, "first", "first", ""));
        Assert.Contains(Key, error.Message);
        Assert.Contains("string[]", error.Message);
    }

    [TestMethod]
    public void RawActivityCapture_MatchingParentSpanInDifferentTrace_FailsParentCheck()
    {
        using var parent = new Activity("parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var child = new Activity("child").Start();
        var snapshot = ActivitySnapshot.Capture(child);
        var expectedParent = parent.Context;
        CapturedActivityAssertions.RequireParentContext(snapshot, expectedParent);

        var wrongTrace = snapshot with { Trace = ActivityTraceId.CreateRandom() };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireParentContext(wrongTrace, expectedParent));
        Assert.Contains("trace changed", error.Message);
        Assert.Contains(parent.TraceId.ToHexString(), error.Message);
    }

    [TestMethod]
    public void RawActivityCapture_DuplicateSpanIdentity_IsRejected()
    {
        using var activity = new Activity("operation").SetIdFormat(ActivityIdFormat.W3C).Start();
        var snapshot = ActivitySnapshot.Capture(activity);
        CapturedActivityAssertions.RequireUniqueSpanIdentities([snapshot]);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireUniqueSpanIdentities([snapshot, snapshot]));
        Assert.Contains("duplicate", error.Message);
    }

    [TestMethod]
    public void RawActivityCaptureReport_HttpAndGrpc_ShareOneSchemaAndPreserveArrayValues()
    {
        string[] httpValues = ["", "a,b"];
        string[] grpcValues = ["AAH/"];
        using var http = new Activity("http").SetIdFormat(ActivityIdFormat.W3C)
            .SetTag(UrlAttributes.Full, "https://example.invalid/ok")
            .SetTag(Key, httpValues).Start();
        using var grpc = new Activity("grpc").SetTag(GrpcAttributes.Method, "/capture.Service/Unary")
            .SetTag("rpc.response.metadata.trace-bin", grpcValues).Start();
        var httpSnapshot = ActivitySnapshot.Capture(http);
        var grpcSnapshot = ActivitySnapshot.Capture(grpc);
        using var document = JsonDocument.Parse(ActivityCaptureReport.Serialize("configured", [httpSnapshot, grpcSnapshot], []));
        var report = document.RootElement;
        Assert.AreEqual(1, report.GetProperty("schema_version").GetInt32());
        Assert.AreEqual("passed", report.GetProperty("status").GetString());
        Assert.AreEqual(2, report.GetProperty("span_count").GetInt32());
        Assert.AreEqual(0, report.GetProperty("after_disposal_spans").GetArrayLength());
        var spans = report.GetProperty("spans");
        Assert.AreEqual("/ok", spans[0].GetProperty("path").GetString());
        Assert.AreEqual("/capture.Service/Unary", spans[1].GetProperty("path").GetString());
        Assert.AreEqual(http.TraceId.ToHexString(), spans[0].GetProperty("trace_id").GetString());
        string?[] capturedValues = [.. spans[0].GetProperty("captured").GetProperty(Key).EnumerateArray().Select(value => value.GetString())];
        Assert.AreSequenceEqual(["", "a,b"], capturedValues);
        Assert.AreEqual("AAH/", spans[1].GetProperty("captured").GetProperty("rpc.response.metadata.trace-bin")[0].GetString());
    }

    [TestMethod]
    public void RawActivityCaptureReport_StringListForClrArray_DoesNotPass()
    {
        List<string> invalidValues = ["value"];
        using var activity = new Activity("wrong-clr-type").SetTag(Key, invalidValues).Start();
        var snapshot = ActivitySnapshot.Capture(activity);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => ActivityCaptureReport.Serialize("configured", [snapshot], []));
        Assert.Contains("expected CLR string[]", error.Message);
    }

    [TestMethod]
    public void RawActivityCaptureDisposal_MissingNativeSpanOrLeftoverCapture_Fails()
    {
        using var activity = new Activity("after-disposal").SetIdFormat(ActivityIdFormat.W3C).Start();
        var snapshot = ActivitySnapshot.Capture(activity) with { Source = "System.Net.Http" };
        CapturedActivityAssertions.RequireCaptureStopped([snapshot], "System.Net.Http");
        Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireCaptureStopped([], "System.Net.Http"));
        var captured = snapshot with { Tags = new Dictionary<string, object?> { [Key] = new[] { "leftover" } } };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireCaptureStopped([captured], "System.Net.Http"));
        Assert.Contains("outlived", error.Message);
    }

    public enum RawViolation { Missing, Null, Scalar, ObjectArray, List, Reordered, DuplicateLost }
}
