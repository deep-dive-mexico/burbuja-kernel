namespace Burbuja.Integrations;

/// <summary>Bounds each JSON/SSE HTTP response before the protocol decoder buffers it. Redirects cannot forward credentials.</summary>
internal sealed class McpResponseLimitHandler(int maxBytes) : DelegatingHandler(
    new HttpClientHandler { AllowAutoRedirect = false })
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        var original = response.Content;
        if (original.Headers.ContentLength > maxBytes)
        {
            response.Dispose();
            throw new McpTransportException("MCP HTTP response exceeded its byte bound.");
        }
        var content = new StreamContent(new LimitedStream(await original.ReadAsStreamAsync(ct), maxBytes, original));
        foreach (var header in original.Headers)
            content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = content;
        return response;
    }

    private sealed class LimitedStream(Stream inner, int maximum, HttpContent owner) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            Account(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            Account(await inner.ReadAsync(buffer, ct).ConfigureAwait(false));
        private int Account(int count)
        {
            _read += count;
            if (_read > maximum)
                throw new McpTransportException("MCP HTTP response exceeded its byte bound.");
            return count;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                owner.Dispose();
            base.Dispose(disposing);
        }
    }
}
