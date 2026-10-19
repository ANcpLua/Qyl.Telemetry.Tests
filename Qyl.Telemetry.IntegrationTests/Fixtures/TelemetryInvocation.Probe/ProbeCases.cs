using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Qyl.Telemetry.Tests;

[assembly: Parallelize(Scope = ExecutionScope.MethodLevel)]

namespace Qyl.Telemetry.IntegrationTests.Probes;

[TracePerTest]
public sealed class ProbeCases(TestContext context)
{
    [ProbeMethod]
    [DataRow(ProbeContract.FirstRow)]
    [DataRow(ProbeContract.SecondRow)]
    public void RowsKeepTheirTraceAndOriginalExecutor(string value)
    {
        var activity = Activity.Current;
        Assert.IsNotNull(activity);
        Assert.AreEqual(TracePerTestAttribute.ActivityName, activity.OperationName);
        context.WriteLine($"{ProbeContract.RowMarker}|{value}|{activity.TraceId.ToHexString()}");
    }

    [TestMethod]
    public void OriginalAssertionFailure() => Assert.Fail(ProbeContract.AssertionMessage);

    [TestMethod]
    public void OriginalExceptionFailure() => throw new InvalidOperationException(ProbeContract.ExceptionMessage);

    [TestMethod]
    public void OriginalInconclusiveResult() => Assert.Inconclusive(ProbeContract.InconclusiveMessage);
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ProbeMethodAttribute(
    [CallerFilePath] string path = "", [CallerLineNumber] int line = -1) : TestMethodAttribute(path, line)
{
    [Experimental("MSTESTEXP")]
    public override async Task<TestResult[]> ExecuteAsync(ITestMethod testMethod)
    {
        TestContext.Current!.WriteLine(ProbeContract.ExecutorMarker);
        return await base.ExecuteAsync(testMethod).ConfigureAwait(false);
    }
}
