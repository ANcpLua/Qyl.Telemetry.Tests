namespace Qyl.Telemetry.IntegrationTests;

/// <summary>An HTTP handler that answers each request with the response <paramref name="respond" /> produces for it.</summary>
/// <param name="respond">Produces the response for one request; it may wait on the request's cancellation.</param>
internal sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request, cancellationToken);
}
