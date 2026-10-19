using System.Runtime.CompilerServices;

namespace Qyl.Telemetry.IntegrationTests.Probes;

[JetBrains.Annotations.UsedImplicitly(JetBrains.Annotations.ImplicitUseTargetFlags.WithMembers)]
internal static class ProbeContract
{
    internal const string FirstRow = "first";
    internal const string SecondRow = "second";
    internal const string RowMethod = "RowsKeepTheirTraceAndOriginalExecutor";
    internal const string AssertionMethod = "OriginalAssertionFailure";
    internal const string ExceptionMethod = "OriginalExceptionFailure";
    internal const string InconclusiveMethod = "OriginalInconclusiveResult";
    internal const string AssertionMessage = "probe original assertion";
    internal const string ExceptionMessage = "probe original exception";
    internal const string InconclusiveMessage = "probe original inconclusive";
    internal const string ExecutorMarker = "original-executor";
    internal const string RowMarker = "probe-row";
    internal const string CasesFileName = "ProbeCases.cs";

    internal static string CasesSourcePath => Path.Combine(Path.GetDirectoryName(SourcePath())!, CasesFileName);

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
