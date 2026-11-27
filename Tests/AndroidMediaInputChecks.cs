using System.Net;
using System.Net.Http.Headers;
using StreamSift.Mobile;

namespace StreamSift;

internal static class AndroidMediaInputChecks
{
    private static readonly Uri Origin = new("https://media.example/audio");
    private static readonly Uri Final = new("https://cdn.example/library/audio");

    internal static async Task<int> RunAsync()
    {
        using var client = new HttpClient(new Responses(request =>
        {
            if (request.Headers.Range?.ToString() != "bytes=2-4" ||
                !request.Headers.TryGetValues("Icy-MetaData", out var icy) || icy.Single() != "1")
                throw new InvalidOperationException("A checked redirect must preserve the format-specific request headers.");
            return request.RequestUri == Origin ? Redirect(Final) : Partial(2, 4, 10, [2, 3, 4]);
        }));
        using var input = await AndroidMediaInput.OpenAsync(client, Origin, 2, 3, true, CancellationToken.None);
        byte[] buffer = new byte[8];
        if (input.Address != Final || input.Total != 10 || input.Length != 3 ||
            input.Read(buffer, CancellationToken.None) != 3 || !buffer.AsSpan(0, 3).SequenceEqual(new byte[] { 2, 3, 4 }) ||
            input.Read(buffer, CancellationToken.None) != 0)
            throw new InvalidOperationException("Checked playback lost the final address or exact byte range.");

        using var noRanges = new HttpClient(new Responses(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4])
        }));
        using var sequential = await AndroidMediaInput.OpenAsync(noRanges, Origin, 2, 1, false, CancellationToken.None);
        if (sequential.Read(buffer, CancellationToken.None) != 1 || buffer[0] != 3 ||
            sequential.Read(buffer, CancellationToken.None) != 0)
            throw new InvalidOperationException("Playback must handle a server that returns the full file.");

        using var wrongRange = new HttpClient(new Responses(_ => Partial(1, 3, 10, [1, 2, 3])));
        await RejectAsync(wrongRange, Origin, 2, 3);

        using var outOfBounds = new HttpClient(new Responses(_ => Partial(2, 5, 10, [2, 3, 4, 5])));
        await RejectAsync(outOfBounds, Origin, 2, 3);

        int requests = 0;
        using var privateRedirect = new HttpClient(new Responses(_ =>
        {
            requests++;
            return Redirect(new Uri("https://127.0.0.1/"));
        }));
        await RejectAsync(privateRedirect, Origin);
        if (requests != 1) throw new InvalidOperationException("Playback must stop before following a disallowed redirect.");

        using var forbidden = new HttpClient(new Responses(_ => throw new InvalidOperationException("A forbidden URL reached HTTP.")));
        await RejectAsync(forbidden, new Uri("https://192.168.1.1/"));
        await RejectAsync(forbidden, new Uri("http://media.example/audio"));
        await RejectAsync(forbidden, new Uri("file:///audio"));

        using var atEnd = new HttpClient(new Responses(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(4);
            return response;
        }));
        using var empty = await AndroidMediaInput.OpenAsync(atEnd, Origin, 4, null, false, CancellationToken.None);
        if (empty.Total != 4 || empty.Length != 0 || empty.Read(buffer, CancellationToken.None) != 0)
            throw new InvalidOperationException("An exact end position must be readable as EOF.");
        return 9;
    }

    private static async Task RejectAsync(HttpClient client, Uri address, long offset = 0, long? length = null)
    {
        try
        {
            using var unexpected = await AndroidMediaInput.OpenAsync(client, address, offset, length, false, CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or HttpRequestException) { return; }
        throw new InvalidOperationException("Playback accepted an invalid response or address.");
    }

    private static HttpResponseMessage Redirect(Uri target) =>
        new(HttpStatusCode.Redirect) { Headers = { Location = target } };

    private static HttpResponseMessage Partial(long from, long to, long total, byte[] data)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        return response;
    }

    private sealed class Responses(Func<HttpRequestMessage, HttpResponseMessage> result) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(result(request));
    }
}
