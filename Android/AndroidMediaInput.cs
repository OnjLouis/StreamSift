using System.Net;
using System.Net.Http.Headers;

namespace StreamSift.Mobile;

internal sealed class AndroidMediaInput : IDisposable
{
    private static readonly HttpClient Client = PublicNetwork.CreateByteRangeClient();
    private static readonly TimeSpan NetworkLimit = TimeSpan.FromSeconds(8);
    private readonly HttpResponseMessage response;
    private readonly Stream stream;
    private long? remaining;

    private AndroidMediaInput(HttpResponseMessage response, Stream stream, Uri address, long? total, long? length)
    {
        this.response = response;
        this.stream = stream;
        Address = response.RequestMessage?.RequestUri ?? address;
        Total = total;
        Length = remaining = length;
    }

    internal Uri Address { get; }
    internal long? Total { get; }
    internal long? Length { get; }

    internal IEnumerable<KeyValuePair<string, IEnumerable<string>>> Headers =>
        response.Headers.Concat(response.Content.Headers);

    internal static AndroidMediaInput Open(Uri address, long offset, long? length, bool icy, CancellationToken token) =>
        OpenAsync(Client, address, offset, length, icy, token).GetAwaiter().GetResult();

    internal static async Task<AndroidMediaInput> OpenAsync(HttpClient client, Uri address, long offset,
        long? length, bool icy, CancellationToken token)
    {
        if (offset < 0 || length is <= 0 || (length.HasValue && length.Value - 1 > long.MaxValue - offset))
            throw new IOException("Invalid audio byte range.");
        var range = offset == 0 && length == null ? null :
            new RangeHeaderValue(offset, length.HasValue ? offset + (length.Value - 1) : null);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(NetworkLimit);
        var response = await PublicMediaResponse.OpenAsync(client, address, deadline.Token, icy, range).ConfigureAwait(false);
        try
        {
            var content = response.Content.Headers;
            var contentRange = content.ContentRange;
            if (range != null && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                contentRange is { Unit: "bytes", HasRange: false, Length: long end } && end == offset)
                return new AndroidMediaInput(response, Stream.Null, address, end, 0);

            response.EnsureSuccessStatusCode();
            if (content.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Encoded audio byte ranges are unsupported.");

            long? total;
            long? available;
            long skip = 0;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (contentRange is not { Unit: "bytes", From: long from, To: long to } ||
                    from != offset || to < from || to - from == long.MaxValue ||
                    (contentRange.Length is long maximum && to >= maximum) ||
                    (range?.Ranges.Single().To is long last && to > last))
                    throw new IOException("The audio server returned a different byte range.");
                total = contentRange.Length;
                available = to - from + 1;
                if (content.ContentLength is long advertised && advertised != available)
                    throw new IOException("The audio byte range length is invalid.");
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                total = content.ContentLength;
                if (total is long size && offset > size)
                    throw new IOException("The requested audio position is past the end.");
                available = total - offset;
                skip = offset;
            }
            else
                throw new IOException("The audio server did not return media bytes.");

            if (length is long requested)
                available = available is long bytes ? Math.Min(bytes, requested) : requested;
            var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            if (skip > 0)
            {
                byte[] scratch = new byte[65536];
                while (skip > 0)
                {
                    int read = await stream.ReadAsync(scratch.AsMemory(0, (int)Math.Min(skip, scratch.Length)),
                        deadline.Token).ConfigureAwait(false);
                    if (read == 0) throw new IOException("The requested audio position is past the end.");
                    skip -= read;
                }
            }
            return new AndroidMediaInput(response, stream, address, total, available);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    internal int Read(Memory<byte> buffer, CancellationToken token)
    {
        int limit = (int)Math.Min(buffer.Length, remaining ?? buffer.Length);
        if (limit == 0) return 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(NetworkLimit);
        int count = stream.ReadAsync(buffer[..limit], deadline.Token).AsTask().GetAwaiter().GetResult();
        remaining -= count;
        return count;
    }

    public void Dispose() => response.Dispose();
}
