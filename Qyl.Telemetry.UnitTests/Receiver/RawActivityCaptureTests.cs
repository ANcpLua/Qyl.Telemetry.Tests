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
    private const string DeprecatedGrpcMetadata = "rpc.grpc.request.metadata.authorization";
    private const string RepeatedKey = "probe.repeated";
    private const string NumericArrayKey = "probe.status_codes";

    [TestMethod]
    public void RawActivityCaptureSnapshot_UrlWithQuery_KeepsOnlyThePath()
    {
        Assert.AreEqual("/ok", MutatedAfterCapture().Path);
    }

    [TestMethod]
    public void RawActivityCaptureSnapshot_ArrayMutatedOrReplacedAfterCapture_KeepsTheOriginalValues()
    {
        CapturedActivityAssertions.RequireStringValues(MutatedAfterCapture().Tags, Key, "first", "first", "", "comma,kept");
    }

    [TestMethod]
    public void RawActivityCaptureSnapshot_TagKeyAddedTwice_KeepsTheValueTheActivityReports()
    {
        using var activity = new Activity("repeated-tag").AddTag(RepeatedKey, "first").AddTag(RepeatedKey, "second").Start();
        Assert.AreEqual(activity.GetTagItem(RepeatedKey), ActivitySnapshot.Capture(activity).Tags[RepeatedKey]);
    }

    [TestMethod]
    public void RawActivityCaptureSnapshot_Int64ArrayMutatedAfterCapture_KeepsTheOriginalValues()
    {
        long[] statusCodes = [200, 404];
        using var activity = new Activity("numeric-array").SetTag(NumericArrayKey, statusCodes).Start();
        var snapshot = ActivitySnapshot.Capture(activity);
        statusCodes[0] = 500;
        Assert.AreSequenceEqual([200L, 404L], Assert.IsInstanceOfType<long[]>(snapshot.Tags[NumericArrayKey]));
    }

    [TestMethod]
    [DataRow(RawViolation.Missing)]
    [DataRow(RawViolation.Null)]
    [DataRow(RawViolation.Scalar)]
    [DataRow(RawViolation.ObjectArray)]
    [DataRow(RawViolation.List)]
    [DataRow(RawViolation.Reordered)]
    [DataRow(RawViolation.DuplicateLost)]
    public void RawActivityCapture_WrongClrTypeOrChangedSequence_FailsNamingTheKey(RawViolation violation)
    {
        var tags = Tags(violation);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireStringValues(tags, Key, "first", "first", ""));
        Assert.Contains(Key, error.Message);
    }

    [TestMethod]
    [DataRow(RawViolation.Missing)]
    [DataRow(RawViolation.Null)]
    [DataRow(RawViolation.Scalar)]
    [DataRow(RawViolation.ObjectArray)]
    [DataRow(RawViolation.List)]
    [DataRow(RawViolation.Reordered)]
    [DataRow(RawViolation.DuplicateLost)]
    public void RawActivityCapture_WrongClrTypeOrChangedSequence_FailsNamingTheExpectedStringArray(RawViolation violation)
    {
        var tags = Tags(violation);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireStringValues(tags, Key, "first", "first", ""));
        Assert.Contains("string[]", error.Message);
    }

    [TestMethod]
    public void RawActivityCapture_ChildOfTheExpectedParent_PassesParentCheck()
    {
        using var parent = new Activity("parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var child = new Activity("child").Start();
        CapturedActivityAssertions.RequireParentContext(ActivitySnapshot.Capture(child), parent.Context);
    }

    [TestMethod]
    public void RawActivityCapture_MatchingParentSpanInDifferentTrace_FailsAsTraceChanged()
    {
        using var parent = new Activity("parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var child = new Activity("child").Start();
        var wrongTrace = ActivitySnapshot.Capture(child) with { Trace = ActivityTraceId.CreateRandom() };
        var parentContext = parent.Context;
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireParentContext(wrongTrace, parentContext));
        Assert.Contains("trace changed", error.Message);
    }

    [TestMethod]
    public void RawActivityCapture_MatchingParentSpanInDifferentTrace_FailsNamingTheExpectedTrace()
    {
        using var parent = new Activity("parent").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var child = new Activity("child").Start();
        var wrongTrace = ActivitySnapshot.Capture(child) with { Trace = ActivityTraceId.CreateRandom() };
        var parentContext = parent.Context;
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireParentContext(wrongTrace, parentContext));
        Assert.Contains(parent.TraceId.ToHexString(), error.Message);
    }

    [TestMethod]
    public void RawActivityCapture_UniqueSpanIdentity_Passes()
    {
        using var activity = new Activity("operation").SetIdFormat(ActivityIdFormat.W3C).Start();
        CapturedActivityAssertions.RequireUniqueSpanIdentities([ActivitySnapshot.Capture(activity)]);
    }

    [TestMethod]
    public void RawActivityCapture_DuplicateSpanIdentity_IsRejected()
    {
        using var activity = new Activity("operation").SetIdFormat(ActivityIdFormat.W3C).Start();
        var snapshot = ActivitySnapshot.Capture(activity);
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireUniqueSpanIdentities([snapshot, snapshot]));
        Assert.Contains("duplicate", error.Message);
    }

    [TestMethod]
    public void RawActivityCaptureReport_HttpAndGrpc_UseSchemaVersionOne()
    {
        using var report = HttpAndGrpcReport(out _);
        Assert.AreEqual(1, report.RootElement.GetProperty("schema_version").GetInt32());
    }

    [TestMethod]
    public void RawActivityCaptureReport_ValidCapture_HasPassedStatus()
    {
        using var report = HttpAndGrpcReport(out _);
        Assert.AreEqual("passed", report.RootElement.GetProperty("status").GetString());
    }

    [TestMethod]
    public void RawActivityCaptureReport_HttpAndGrpc_CountBothSpans()
    {
        using var report = HttpAndGrpcReport(out _);
        Assert.AreEqual(2, report.RootElement.GetProperty("span_count").GetInt32());
    }

    [TestMethod]
    public void RawActivityCaptureReport_NoLateSpans_HasNoAfterDisposalSpans()
    {
        using var report = HttpAndGrpcReport(out _);
        Assert.AreEqual(0, report.RootElement.GetProperty("after_disposal_spans").GetArrayLength());
    }

    [TestMethod]
    public void RawActivityCaptureReport_HttpSpan_HasTheUrlPath()
    {
        using var report = HttpAndGrpcReport(out _);
        Assert.AreEqual("/ok", report.RootElement.GetProperty("spans")[0].GetProperty("path").GetString());
    }

    [TestMethod]
    public void RawActivityCaptureReport_GrpcSpan_HasTheMethodAsPath()
    {
        using var report = HttpAndGrpcReport(out _);
        Assert.AreEqual("/capture.Service/Unary", report.RootElement.GetProperty("spans")[1].GetProperty("path").GetString());
    }

    [TestMethod]
    public void RawActivityCaptureReport_HttpSpan_HasItsTraceId()
    {
        using var report = HttpAndGrpcReport(out var httpTraceId);
        Assert.AreEqual(httpTraceId, report.RootElement.GetProperty("spans")[0].GetProperty("trace_id").GetString());
    }

    [TestMethod]
    public void RawActivityCaptureReport_HttpHeaderArray_KeepsEmptyAndCommaValues()
    {
        using var report = HttpAndGrpcReport(out _);
        string?[] capturedValues = [.. report.RootElement.GetProperty("spans")[0].GetProperty("captured").GetProperty(Key).EnumerateArray().Select(value => value.GetString())];
        Assert.AreSequenceEqual(["", "a,b"], capturedValues);
    }

    [TestMethod]
    public void RawActivityCaptureReport_GrpcBinaryMetadata_KeepsItsValue()
    {
        using var report = HttpAndGrpcReport(out _);
        Assert.AreEqual("AAH/", report.RootElement.GetProperty("spans")[1].GetProperty("captured").GetProperty("rpc.response.metadata.trace-bin")[0].GetString());
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
    public void RawActivityCaptureDisposal_StoppedNativeSpan_Passes()
    {
        using var activity = new Activity("after-disposal").SetIdFormat(ActivityIdFormat.W3C).Start();
        CapturedActivityAssertions.RequireCaptureStopped([NativeHttpSnapshot(activity)], NativeHttpSource);
    }

    [TestMethod]
    public void RawActivityCaptureDisposal_MissingNativeSpan_Fails()
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireCaptureStopped([], NativeHttpSource));
    }

    [TestMethod]
    [DataRow(Key)]
    [DataRow(DeprecatedGrpcMetadata)]
    public void RawActivityCaptureDisposal_LeftoverCapturedField_FailsAsOutlived(string field)
    {
        using var activity = new Activity("after-disposal").SetIdFormat(ActivityIdFormat.W3C).Start();
        var captured = NativeHttpSnapshot(activity) with { Tags = new Dictionary<string, object?> { [field] = new[] { "leftover" } } };
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => CapturedActivityAssertions.RequireCaptureStopped([captured], NativeHttpSource));
        Assert.Contains("outlived", error.Message);
    }

    private const string NativeHttpSource = "System.Net.Http";

    private static ActivitySnapshot NativeHttpSnapshot(Activity activity) => ActivitySnapshot.Capture(activity) with { Source = NativeHttpSource };

    private static ActivitySnapshot MutatedAfterCapture()
    {
        string[] values = ["first", "first", "", "comma,kept"];
        using var activity = new Activity("http").SetTag(UrlAttributes.Full, "https://example.invalid/ok?ignored=true")
            .SetTag(Key, values).Start();
        var snapshot = ActivitySnapshot.Capture(activity);
        values[0] = "mutated";
        string[] replacement = ["replaced"];
        activity.SetTag(Key, replacement);
        return snapshot;
    }

    private static Dictionary<string, object?> Tags(RawViolation violation)
    {
        var tags = new Dictionary<string, object?>();
        if (violation != RawViolation.Missing)
        {
            tags[Key] = violation switch
            {
                RawViolation.Missing => throw new UnreachableException("handled above"),
                RawViolation.Null => null,
                RawViolation.Scalar => "first",
                RawViolation.ObjectArray => new object[] { "first", "first", "" },
                RawViolation.List => new List<string> { "first", "first", "" },
                RawViolation.Reordered => new[] { "", "first", "first" },
                RawViolation.DuplicateLost => new[] { "first", "" },
                _ => throw new ArgumentOutOfRangeException(nameof(violation))
            };
        }
        return tags;
    }

    private static JsonDocument HttpAndGrpcReport(out string httpTraceId)
    {
        string[] httpValues = ["", "a,b"];
        string[] grpcValues = ["AAH/"];
        using var http = new Activity("http").SetIdFormat(ActivityIdFormat.W3C)
            .SetTag(UrlAttributes.Full, "https://example.invalid/ok")
            .SetTag(Key, httpValues).Start();
        using var grpc = new Activity("grpc").SetTag(GrpcAttributes.Method, "/capture.Service/Unary")
            .SetTag("rpc.response.metadata.trace-bin", grpcValues).Start();
        httpTraceId = http.TraceId.ToHexString();
        return JsonDocument.Parse(ActivityCaptureReport.Serialize("configured", [ActivitySnapshot.Capture(http), ActivitySnapshot.Capture(grpc)], []));
    }

    public enum RawViolation { Missing, Null, Scalar, ObjectArray, List, Reordered, DuplicateLost }
}
