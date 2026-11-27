using System.Text.Json;

namespace StreamSift.Mobile;

internal static class AndroidReleaseCheck
{
    private const string Repository = "OnjLouis/StreamSift";
    private const string TagPrefix = "android-v";
    private const int PageSize = 100;
    private const int MaximumPages = 5;
    private const long MaximumCatalogBytes = 2 * 1024 * 1024;

    internal static string ReleasePage(Version version) =>
        $"https://github.com/{Repository}/releases/tag/{TagPrefix}{version.ToString(3)}";

    internal static async Task<Version?> CheckAsync(HttpClient client, Version current, CancellationToken token)
    {
        client.MaxResponseContentBufferSize = MaximumCatalogBytes;
        client.DefaultRequestHeaders.UserAgent.ParseAdd("StreamSift-Android");
        Version? latest = null;
        for (int page = 1; page <= MaximumPages; page++)
        {
            using var response = await client.GetAsync(
                $"https://api.github.com/repos/{Repository}/releases?per_page={PageSize}&page={page}", token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var catalog = await JsonDocument.ParseAsync(body, cancellationToken: token).ConfigureAwait(false);
            if (catalog.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("The Android release list could not be read.");

            foreach (var release in catalog.RootElement.EnumerateArray())
            {
                if (ReleaseVersion(release) is { } version && version > current &&
                    (latest == null || version > latest))
                    latest = version;
            }

            if (catalog.RootElement.GetArrayLength() < PageSize) return latest;
        }
        throw new InvalidDataException("The Android release list exceeded the search limit.");
    }

    private static Version? ReleaseVersion(JsonElement release)
    {
        if (release.ValueKind != JsonValueKind.Object ||
            !release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False ||
            !release.TryGetProperty("tag_name", out var name) || name.ValueKind != JsonValueKind.String ||
            name.GetString() is not { } tag || !tag.StartsWith(TagPrefix, StringComparison.Ordinal) ||
            !Version.TryParse(tag.AsSpan(TagPrefix.Length), out var version) ||
            version.Build < 0 || version.Revision != -1 || tag != TagPrefix + version.ToString(3) ||
            !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        string apkName = $"StreamSift-Android-{version.ToString(3)}.apk";
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind == JsonValueKind.Object &&
                asset.TryGetProperty("name", out var assetName) && assetName.ValueKind == JsonValueKind.String &&
                assetName.GetString() == apkName &&
                asset.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String &&
                state.GetString() == "uploaded" &&
                asset.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Number &&
                size.TryGetInt64(out long bytes) && bytes > 0)
                return version;
        }
        return null;
    }
}
