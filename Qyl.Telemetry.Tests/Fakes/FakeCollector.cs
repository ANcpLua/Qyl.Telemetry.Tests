using System.Net;
using System.Text;
using Qyl.Telemetry.Tests.Collector;

namespace Qyl.Telemetry.Tests.Fakes;

/// <summary>Answers Collector reads in sequence and exposes their arrival without opening a network listener.</summary>
internal sealed class FakeCollector(IReadOnlyList<string?> responses) : IDisposable
{
    private readonly TaskCompletionSource _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<HttpClient> _clients = [];
    private int _reads;

    public Task FirstRead => _firstRead.Task;
    public Task SecondRead => _secondRead.Task;
    public int Reads => Volatile.Read(ref _reads);

    public CollectorClient Client(TimeProvider clock)
    {
        var client = new HttpClient(new StubHandler(_ => Respond())) { BaseAddress = new Uri("https://qyl.test/") };
        _clients.Add(client);
        return new CollectorClient(client, clock);
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
    }

    private HttpResponseMessage Respond()
    {
        var read = Interlocked.Increment(ref _reads);
        var body = responses[Math.Min(read - 1, responses.Count - 1)];
        switch (read)
        {
            case 1:
                _firstRead.TrySetResult();
                break;
            case 2:
                _secondRead.TrySetResult();
                break;
        }
        return body is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
