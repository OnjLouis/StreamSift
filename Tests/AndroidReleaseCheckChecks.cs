using System.Net;
using System.Text.Json;
using StreamSift.Mobile;

namespace StreamSift;

internal static class AndroidReleaseCheckChecks
{
    internal static async Task<int> RunAsync()
    {
        var firstPage = Enumerable.Repeat(Release("v9.0.0", "StreamSift-Android-9.0.0.apk"), 100).ToArray();
        var secondPage = new[]
        {
            Release("android-v1.3.2", "StreamSift-Android-1.3.2.apk"),
            Release("android-v1.3.3", "StreamSift-Android-1.3.3.apk"),
            Release("android-v9.0.1", "StreamSift-Android-9.0.1.apk", draft: true),
            Release("android-v9.0.2", "StreamSift-Android-9.0.2.apk", prerelease: true),
            Release("android-v9.0.3", "not-the-apk.zip"),
            Release("android-v9.0.4", "StreamSift-Android-9.0.4.apk", state: "new"),
            Release("android-v9.0.5", "StreamSift-Android-9.0.5.apk", size: 0)
        };
        using var client = new HttpClient(new Catalog(firstPage, secondPage));
        var update = await AndroidReleaseCheck.CheckAsync(client, new Version(1, 3, 1), CancellationToken.None);
        if (update != new Version(1, 3, 3))
            throw new InvalidOperationException("Select the newest published Android release with a complete APK asset.");
        if (AndroidReleaseCheck.ReleasePage(update) != "https://github.com/OnjLouis/StreamSift/releases/tag/android-v1.3.3")
            throw new InvalidOperationException("Calculate the official release page without accepting a response-provided URL.");

        using var current = new HttpClient(new Catalog(secondPage));
        if (await AndroidReleaseCheck.CheckAsync(current, new Version(1, 3, 3), CancellationToken.None) != null)
            throw new InvalidOperationException("Do not offer the installed version again.");
        return 3;
    }

    private static object Release(string tag, string asset, bool draft = false, bool prerelease = false,
        string state = "uploaded", long size = 100) =>
        new { tag_name = tag, draft, prerelease, assets = new[] { new { name = asset, state, size } } };

    private sealed class Catalog(params object[][] pages) : HttpMessageHandler
    {
        private int nextPage;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string expected = $"https://api.github.com/repos/OnjLouis/StreamSift/releases?per_page=100&page={nextPage + 1}";
            if (request.RequestUri?.AbsoluteUri != expected || nextPage >= pages.Length)
                throw new InvalidOperationException("Unexpected catalog request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(pages[nextPage++]))
            });
        }
    }
}
