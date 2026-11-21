using System.Net;

namespace Promin.Master.Core.Tests.TestDoubles;

/// <summary>Answers each request from <see cref="Responses"/> by its full URL, 404 for anything else, and records it.</summary>
public sealed class StubHttpHandler : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, byte[] Body)> Responses { get; } = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>When set, every request throws it, as an unreachable network does.</summary>
    public Exception? Throw { get; set; }

    /// <summary>URLs whose answer arrives but whose body never sends a byte, as a dropped connection does.</summary>
    public HashSet<string> Stalled { get; } = new(StringComparer.Ordinal);

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

        if (Stalled.Contains(request.RequestUri!.ToString()))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        }

        var response = Responses.TryGetValue(request.RequestUri!.ToString(), out var answer)
            ? new HttpResponseMessage(answer.Status) { Content = new ByteArrayContent(answer.Body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new ByteArrayContent([]) };
        return Task.FromResult(response);
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
