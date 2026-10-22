namespace Qyl.Telemetry.UnitTests;

/// <summary>An HTTP handler that answers each request with the response <paramref name="respond" /> builds for it.</summary>
/// <param name="respond">Builds the response for one request.</param>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(respond(request));
    }
}
