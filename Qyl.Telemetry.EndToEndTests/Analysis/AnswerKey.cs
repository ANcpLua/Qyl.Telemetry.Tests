using System.Globalization;
using System.Text.Json.Nodes;
using OpenTelemetry.Proto.Metrics.V1;
using static Qyl.Telemetry.EndToEndTests.Analysis.OtlpIncidents;

namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>
///     Independent incidents with known answers, after qyl's seed-answer-key.py: a database timeout that
///     propagates to checkout under release 1.4.1, a rare payment failure, a silent service, one failing session,
///     a regional discriminator, a fix window, latency routes and two metrics.
/// </summary>
internal sealed class AnswerKey
{
    internal const string Release = "1.4.1";
    internal const int FixRequestsNeeded = 96;
    internal const double CheckoutSeconds = 1.84;
    internal const long CounterIncrease = 90;

    internal AnswerKey(DateTimeOffset now)
    {
        Now = Nanoseconds(now);
        T = Now - 20 * Minute;
        T2 = Now - 5 * Minute;
        Seed();
    }

    internal ulong Now { get; }
    internal ulong T { get; }
    internal ulong T2 { get; }
    internal OtlpIncidents Incidents { get; } = new();
    internal SpanKey this[string key] => Incidents.Keys[key];

    internal JsonObject Window() => new() { ["start_time"] = Iso(Now - 60 * Minute), ["end_time"] = Iso(Now + Minute) };

    internal static string Iso(ulong nanoseconds) =>
        Time(nanoseconds).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    private void Seed()
    {
        foreach (var (session, count, errors) in new[] { ("sess_ok", 12, 0), ("sess_bad", 9, 3) })
            for (var i = 0; i < count; i++)
                Incidents.Span($"{session}-{i}", "sessions", "request", Now - Minute + (ulong)i * Millisecond, error: i < errors,
                    session: session, attributes: i < errors ? new Dictionary<string, object> { ["error.type"] = "test" } : null);

        var failing = Version(Release);
        var root = Incidents.Span("db-root", "checkout", "checkout", T + Minute, 200, resource: failing);
        Incidents.Span("db-timeout", "orders-db", "db", T + Minute + Millisecond, 100, error: true, parent: root.SpanId,
            trace: root.TraceId, attributes: new Dictionary<string, object> { ["error.type"] = "timeout", ["db.system.name"] = "postgresql" },
            message: "database timeout");
        for (var i = 0; i < 5; i++)
            Incidents.Span($"checkout-child-{i}", "checkout", "checkout", T + Minute + (110 + (ulong)i) * Millisecond, 5, error: true,
                parent: root.SpanId, trace: root.TraceId, attributes: new Dictionary<string, object> { ["error.type"] = "downstream" },
                resource: failing, message: "dependency failed");
        var success = Incidents.Span("db-success-root", "checkout", "checkout", T - Minute, 180, resource: Version("1.4.0"));
        Incidents.Span("db-success", "orders-db", "db", T - Minute + Millisecond, 90, parent: success.SpanId, trace: success.TraceId,
            attributes: new Dictionary<string, object> { ["db.system.name"] = "postgresql" });
        for (var i = 0; i < 30; i++)
            Incidents.Span($"baseline-{i}", "checkout", "checkout", Now - 80 * Minute + (ulong)i * Millisecond, resource: Version("1.4.0"));
        Incidents.Span("release-first", "checkout", "checkout", T, error: true,
            attributes: new Dictionary<string, object> { ["error.type"] = "downstream" }, resource: failing, message: "dependency failed");
        for (var i = 0; i < 4; i++)
            Incidents.Span($"fix-request-{i}", "checkout", "checkout", T2 + Minute + (ulong)i * Millisecond, resource: failing);
        for (var i = 0; i < 2; i++)
            Incidents.Span($"rare-{i}", "payments", "/pay", T + (ulong)i * Millisecond, error: true, attributes: new Dictionary<string, object>
            {
                ["http.route"] = "/pay", ["http.response.status_code"] = 500, ["error.type"] = "payment"
            }, message: "payment error");
        foreach (var (region, version, error) in new[] { ("eu", "2.3", true), ("us", "2.3", false), ("eu", "2.2", false), ("us", "2.2", false) })
            for (var i = 0; i < 10; i++)
                Incidents.Span($"discrim-{region}-{version}-{i}", "regional", "request", T + (ulong)i * Millisecond, error: error,
                    attributes: error ? new Dictionary<string, object> { ["error.type"] = "regional" } : null,
                    resource: new Dictionary<string, object> { ["cloud.region"] = region, ["service.version"] = version });
        foreach (var (route, milliseconds) in new[] { ("/checkout", 1840d), ("/cart", 210d) })
            for (var i = 0; i < 20; i++)
                Incidents.Span($"latency-{route}-{i}", "http", "GET " + route, T + (ulong)i * Millisecond, milliseconds,
                    attributes: new Dictionary<string, object> { ["http.route"] = route });
        Incidents.Span("silent", "billing", "charge", Now - 120 * Minute);

        foreach (var (route, seconds) in new[] { ("/checkout", CheckoutSeconds), ("/cart", .21) })
            Incidents.Metric("http", new Metric
            {
                Name = "http.server.request.duration", Description = "HTTP duration", Unit = "s",
                Histogram = new Histogram
                {
                    AggregationTemporality = AggregationTemporality.Delta,
                    DataPoints =
                    {
                        new HistogramDataPoint
                        {
                            Attributes = { Pair("http.route", route) }, StartTimeUnixNano = T - Minute, TimeUnixNano = T, Count = 100,
                            Sum = seconds * 100, Min = seconds, Max = seconds, ExplicitBounds = { seconds / 2, seconds, seconds * 1.5 },
                            BucketCounts = { 0, 100, 0, 0 }
                        }
                    }
                }
            });
        var counter = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = true };
        foreach (var (value, i) in new[] { 100L, 150, 190 }.Select((value, i) => (value, (ulong)i)))
            counter.DataPoints.Add(new NumberDataPoint { StartTimeUnixNano = T - Minute, TimeUnixNano = T + i * Minute, AsInt = value });
        Incidents.Metric("counter", new Metric { Name = "requests.total", Unit = "{request}", Sum = counter });
    }

    private static Dictionary<string, object> Version(string version) => new() { ["service.version"] = version };
}
