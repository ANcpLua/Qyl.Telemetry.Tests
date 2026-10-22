namespace Qyl.Telemetry.EndToEndTests.Analysis;

/// <summary>The analysis session one test class shares from its class initialization to its class cleanup.</summary>
internal sealed class SharedAnalysisSession
{
    private AnalysisSession? _session;

    internal AnalysisSession Session => _session ?? throw new InvalidOperationException("The Collector has not started.");

    internal async Task<AnalysisSession> StartAsync(TestContext context) => _session = await AnalysisSession.StartAsync(context);

    internal async Task StopAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }
}
