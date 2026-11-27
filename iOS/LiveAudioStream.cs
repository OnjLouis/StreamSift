using System.Globalization;
using System.Text;

namespace StreamSift.Mobile;

internal sealed class LiveAudioStream(Stream body, CancellationToken stop, int metadataInterval, Action<string?> titleChanged) : Stream
{
    private static readonly TimeSpan NetworkReadLimit = TimeSpan.FromSeconds(8);
    internal const int MetadataBlockSize = 16;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
    private int? untilMetadata;
    private long position;

    internal static int MetadataInterval(HttpResponseMessage response) =>
        response.Headers.TryGetValues("icy-metaint", out var values) &&
        int.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out int interval) &&
        interval > 0 ? interval : 0;

    internal static string? CleanTitle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = string.Concat(text.Trim().Take(512).Select(character => char.IsControl(character) ? ' ' : character)).Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    // NLayer checks the audio offset even on a forward-only stream.
    public override long Position { get => position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;
        untilMetadata ??= metadataInterval;
        if (metadataInterval > 0 && untilMetadata == 0)
        {
            if (!ReadMetadata()) return 0;
            untilMetadata = metadataInterval;
        }
        int read = Receive(buffer.AsMemory(offset, metadataInterval > 0 ? Math.Min(count, untilMetadata.Value) : count));
        if (metadataInterval > 0) untilMetadata -= read;
        position += read;
        return read;
    }

    private bool ReadMetadata()
    {
        var length = new byte[1];
        if (Receive(length) == 0) return false;
        if (length[0] == 0) return true;
        var metadata = new byte[length[0] * MetadataBlockSize];
        int offset = 0;
        while (offset < metadata.Length)
        {
            int read = Receive(metadata.AsMemory(offset));
            if (read == 0) return false;
            offset += read;
        }
        if (TryTitle(metadata, out var title)) titleChanged(title);
        return true;
    }

    internal static bool TryTitle(byte[] metadata, out string? title)
    {
        title = null;
        string text;
        try { text = Utf8.GetString(metadata); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(metadata); }
        const string field = "StreamTitle='";
        int start = text.IndexOf(field, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return false;
        start += field.Length;
        int end = text.IndexOf("';", start, StringComparison.Ordinal);
        if (end < start) return false;
        title = CleanTitle(text[start..end]);
        return true;
    }

    private int Receive(Memory<byte> buffer)
    {
        limit.CancelAfter(NetworkReadLimit);
        return body.ReadAsync(buffer, limit.Token).AsTask().GetAwaiter().GetResult();
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) limit.Dispose();
        base.Dispose(disposing);
    }
}
