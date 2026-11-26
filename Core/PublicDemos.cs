using AngleSharp.Dom;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StreamSift;

internal static class PublicDemos
{
    private const int NativePlaylistLimit = 6;
    private static readonly Regex NativeShortcode = new(
        """\[play\s+type\s*=\s*"audio"\s+id\s*=\s*"([0-9]{1,20})"(?:\s[^\]]{0,512})?\]""",
        RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    private static readonly Uri SonicAudio = new("https://www.soniccouture.com/files/demos/");

    internal static bool IsNativeInstruments(Uri page) => page.Scheme == Uri.UriSchemeHttps && page.IsDefaultPort &&
        page.IdnHost is "www.native-instruments.com" or "native-instruments.com";

    internal static bool IsPlaylist(LinkKind kind) =>
        kind is LinkKind.SoniccoutureDemos or LinkKind.NativeInstrumentsDemos or LinkKind.MagnatunePreviews;

    internal static Link? MagnatuneAlbum(Uri page)
    {
        if (page.Scheme != Uri.UriSchemeHttps || !page.IsDefaultPort || page.IdnHost is not ("magnatune.com" or "www.magnatune.com") ||
            page.Query.Length != 0) return null;
        var sku = page.AbsolutePath.Trim('/').Split('/') switch
        {
            ["artists", "albums", var id] => id,
            ["api", "albums", var id] => id,
            _ => null
        };
        if (sku is not { Length: > 0 and <= 128 } || !sku.All(c => Ascii.IsLetterOrDigit(c) || c is '-' or '_'))
            return null;
        return new(new Uri("https://magnatune.com/api/albums/" + sku),
            new Uri($"https://magnatune.com/artists/albums/{sku}/"), LinkKind.MagnatunePreviews);
    }

    internal static IEnumerable<Link> Advertised(IDocument document, Uri page)
    {
        if (page.Scheme != Uri.UriSchemeHttps || !page.IsDefaultPort) yield break;
        if (page.IdnHost is "www.soniccouture.com" or "soniccouture.com" &&
            page.AbsolutePath.Contains("/products/", StringComparison.Ordinal))
        {
            var button = document.QuerySelector(".play_demos[data-id]:not([data-isgroupid='1'])");
            if (button?.GetAttribute("data-id") is string group && IsId(group))
                yield return new(new Uri(page, "/mp3player/ajax.php"), page, LinkKind.SoniccoutureDemos, DemoGroup: group);
        }
        else if (IsNativeInstruments(page) && document.QuerySelector("main") is IElement main)
        {
            var playerIds = main.QuerySelectorAll("[data-awplayer-product-id]")
                .Select(button => button.GetAttribute("data-awplayer-product-id"))
                .Concat(NativeShortcode.Matches(main.TextContent).Select(match => match.Groups[1].Value));
            foreach (var id in playerIds.OfType<string>().Where(IsId).Distinct().Take(NativePlaylistLimit))
                yield return new(new Uri(page, $"/apps/awplayer/products/{id}/tracks"), page, LinkKind.NativeInstrumentsDemos);
        }
    }

    internal static IEnumerable<Link> Tracks(byte[] content, Link playlist, CancellationToken token)
    {
        using var data = JsonDocument.Parse(content);
        bool sonic = playlist.Kind == LinkKind.SoniccoutureDemos;
        bool magnatune = playlist.Kind == LinkKind.MagnatunePreviews;
        var collection = magnatune ? Field(data.RootElement, "album") : data.RootElement;
        if (magnatune && Text(collection, "sku") != playlist.Url.Segments[^1]) yield break;
        var entries = Field(collection, sonic ? "playlist" : "tracks");
        if (entries.ValueKind != JsonValueKind.Array) yield break;
        foreach (var entry in entries.EnumerateArray().Take(FileIndex.TrackLimit))
        {
            token.ThrowIfCancellationRequested();
            var name = Extraction.Label(Text(entry, sonic ? "Name" : magnatune ? "name" : "title"));
            var address = playlist.Kind switch
            {
                LinkKind.SoniccoutureDemos => SonicTrack(entry),
                LinkKind.MagnatunePreviews => MagnatuneTrack(entry),
                _ => NativeTrack(entry)
            };
            if (magnatune) name = name == null ? "Magnatune preview" : $"{name} preview";
            if (address != null)
                yield return new(address, playlist.FoundOn, LinkKind.Audio, name, true,
                    NameOrigin.Directory, KeepAddress: true, PageAudio: true);
        }
    }

    private static Uri? SonicTrack(JsonElement entry) =>
        Text(entry, "MP3File") is string path && path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) &&
        FileIndex.TryFilePath(path.Split('/'), out var escaped) ? new Uri(SonicAudio, escaped) : null;

    private static Uri? MagnatuneTrack(JsonElement entry) =>
        PublicNetwork.TryUrl(Text(entry, "preview_mp3") ?? "", null, out var address) &&
        address.Scheme == Uri.UriSchemeHttps && address.IsDefaultPort && address.IdnHost == "magnatune.com" &&
        address.Query.Length == 0 && address.AbsolutePath.StartsWith("/music/", StringComparison.Ordinal) &&
        address.AbsolutePath.EndsWith("_spoken.mp3", StringComparison.OrdinalIgnoreCase) ? address : null;

    private static Uri? NativeTrack(JsonElement entry)
    {
        if (!Duration(entry, "duration", out double duration) || !Duration(entry, "previewDuration", out double preview) ||
            preview < duration) return null;
        return PublicNetwork.TryUrl(Text(entry, "url") ?? "", null, out var address) && address.Scheme == Uri.UriSchemeHttps &&
            address.IsDefaultPort && address.IdnHost == "assets.awplayer.com" && address.Query.Length == 0 ? address : null;
    }

    private static bool Duration(JsonElement entry, string field, out double seconds)
    {
        var value = Field(entry, field);
        return (value.ValueKind == JsonValueKind.Number ? value.TryGetDouble(out seconds) :
            double.TryParse(Text(entry, field), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) &&
            double.IsFinite(seconds) && seconds > 0;
    }

    private static JsonElement Field(JsonElement entry, string field) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(field, out var value) ? value : default;

    private static string? Text(JsonElement entry, string field) =>
        Field(entry, field) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool IsId(string value) => value.Length is > 0 and <= 20 && value.All(Ascii.IsDigit);
}
