using System.Text;

namespace StreamSift;

internal static class IcyScanTitle
{
    internal const string RequestHeader = "Icy-MetaData";
    private const int BytesPerBlock = 16, MaximumInterval = 65536;
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMilliseconds(1200);
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static int Interval(string? header) =>
        int.TryParse(header, out int interval) && interval is >= BytesPerBlock and <= MaximumInterval ? interval : 0;

    internal static async Task<string?> ReadAsync(Stream stream, int interval, int audioRead, TimeSpan budget,
        CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(budget < ReadLimit ? budget : ReadLimit);
        var buffer = new byte[4096];
        try
        {
            while (audioRead < interval)
            {
                int size = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(interval - audioRead, buffer.Length)), deadline.Token);
                if (size == 0) return null;
                audioRead += size;
            }
            if (await stream.ReadAsync(buffer.AsMemory(0, 1), deadline.Token) != 1) return null;
            int length = buffer[0] * BytesPerBlock;
            for (int read = 0; read < length;)
            {
                int size = await stream.ReadAsync(buffer.AsMemory(read, length - read), deadline.Token);
                if (size == 0) return null;
                read += size;
            }
            string metadata;
            try { metadata = Utf8.GetString(buffer, 0, length); }
            catch (DecoderFallbackException) { metadata = Encoding.Latin1.GetString(buffer, 0, length); }
            const string field = "StreamTitle='";
            int start = metadata.IndexOf(field, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += field.Length;
            int end = metadata.IndexOf("';", start, StringComparison.Ordinal);
            return end >= start ? Extraction.Label(metadata[start..end]) : null;
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or HttpRequestException)
        {
            return null; // The audio was already verified.
        }
    }
}
