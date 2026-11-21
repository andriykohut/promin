using System.Net;

namespace Promin.Master.Core.Tests.TestDoubles;

/// <summary>Answers each request from <see cref="Responses"/> by its full URL, 404 for anything else, and records it.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, byte[] Body)> Responses { get; } = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>When set, every request throws it, as an unreachable network does.</summary>
    public Exception? Throw { get; set; }

    public void Serve(string url, byte[] body) => Responses[url] = (HttpStatusCode.OK, body);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (Throw is not null)
        {
            throw Throw;
        }

        var response = Responses.TryGetValue(request.RequestUri!.ToString(), out var answer)
            ? new HttpResponseMessage(answer.Status) { Content = new ByteArrayContent(answer.Body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        return Task.FromResult(response);
    }
}
