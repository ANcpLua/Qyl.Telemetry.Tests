using System.Runtime.CompilerServices;

namespace Qyl.Telemetry.IntegrationTests.Probes;

[JetBrains.Annotations.UsedImplicitly(JetBrains.Annotations.ImplicitUseTargetFlags.WithMembers)]
internal static class ProbeContract
{
    internal const string FirstRow = "first";
    internal const string SecondRow = "second";
    internal const string RowMethod = "ProbeRows_EachDataRow_KeepsItsTraceAndOriginalExecutor";
    internal const string AssertionMethod = "Probe_AssertFail_KeepsTheOriginalAssertion";
    internal const string ExceptionMethod = "Probe_Throw_KeepsTheOriginalException";
    internal const string InconclusiveMethod = "Probe_Inconclusive_KeepsTheOriginalResult";
    internal const string AssertionMessage = "probe original assertion";
    internal const string ExceptionMessage = "probe original exception";
    internal const string InconclusiveMessage = "probe original inconclusive";
    internal const string ExecutorMarker = "original-executor";
    internal const string RowMarker = "probe-row";
    internal const string CasesFileName = "ProbeCases.cs";

    internal static string CasesSourcePath => Path.Combine(Path.GetDirectoryName(SourcePath())!, CasesFileName);

    private static string SourcePath([CallerFilePath] string path = "") => path;
}

[JetBrains.Annotations.UsedImplicitly(JetBrains.Annotations.ImplicitUseTargetFlags.WithMembers)]
internal static class PolicyContract
{
    internal const string FirstRow = "first.row";
    internal const string SecondRow = "second.row";
    internal const string Operation = "GET /probe";
    internal const string OriginalMessage = "probe original policy failure";
    internal const string SatisfiedMethod = "Policies_SatisfiedOnEachDataRow_KeepTheRowPassing";
    internal const string WrappedMethod = "NoDuplicateSpans_WrappedOperation_Fails";
    internal const string MissingMethod = "ConnectedTrace_NoEvidence_Fails";
    internal const string OriginalMethod = "ConnectedTrace_OriginalFailure_IsKept";
    internal const string UnverifiedMethod = "PlainTestClass_PolicyAttribute_IsRejected";
}
