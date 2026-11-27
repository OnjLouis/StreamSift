using System.Net;
using System.Net.Http.Headers;

namespace StreamSift.Mobile;

internal static class PublicMediaResponse
{
    internal static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Uri address, CancellationToken token,
        bool requestStreamTitle = false, RangeHeaderValue? range = null)
    {
        const int maxRedirects = 5;
        for (int hop = 0; hop <= maxRedirects; hop++)
        {
            if (!address.IsAbsoluteUri || !PublicNetwork.TryUrl(address.AbsoluteUri, null, out address) || address.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException("The audio address must use public HTTPS.");

            using var request = new HttpRequestMessage(HttpMethod.Get, address)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.UserAgent.ParseAdd("StreamSift/1.3");
            request.Headers.Range = range;
            if (requestStreamTitle) request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308))
            {
                response.RequestMessage ??= request;
                return response;
            }
            using (response)
            {
                if (response.Headers.Location is not Uri target ||
                    !PublicNetwork.TryUrl(target.OriginalString, address, out address))
                    throw new HttpRequestException("The audio redirect is invalid.");
            }
        }
        throw new HttpRequestException("Too many audio redirects.");
    }
}
