using Qyl.Api.Contracts;
using Qyl.Cli.Runtime;
using Qyl.Orchestration.Internal;

namespace Qyl.Telemetry.UnitTests.Collector;

[TestClass]
[TestCategory("Fast")]
public class RunnerContractTests
{
    [TestMethod]
    public void Should_PreserveTicksAndOffset_When_RunnerStateIsSerialized()
    {
        var state = State();
        var wire = ContractJson.Serialize(QylRunnerContractMapper.ToContract(state));
        Assert.AreEqual("2026-10-23T12:34:56.1234567+02:30", wire.GetProperty("timestamp").GetString());
    }

    [TestMethod]
    public void Should_PreserveEscapedUri_When_RunnerEndpointIsSerialized()
    {
        const string Endpoint = "https://example.test/orders%2Fdetail?name=a%20b";
        var state = State() with { Endpoint = new Uri(Endpoint) };
        var wire = ContractJson.Serialize(QylRunnerContractMapper.ToContract(state));
        Assert.AreEqual(Endpoint, wire.GetProperty("endpoint").GetString());
    }

    [TestMethod]
    public void Should_OmitEndpoint_When_RunnerHasNoEndpoint()
    {
        var wire = ContractJson.Serialize(QylRunnerContractMapper.ToContract(State()));
        Assert.IsFalse(wire.TryGetProperty("endpoint", out _));
    }

    private static QylResourceState State() => new()
    {
        Name = "checkout", Lifecycle = ResourceLifecycle.Ready,
        Timestamp = new DateTimeOffset(2026, 10, 23, 12, 34, 56, TimeSpan.FromMinutes(150)).AddTicks(1234567)
    };
}
