using Android.Media;

namespace StreamSift.Mobile;

internal sealed class PublicFileSource(Uri address) : MediaDataSource
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly byte[] window = new byte[65536];
    private AndroidMediaInput? input;
    private long? total;
    private long windowStart;
    private int windowLength;
    private int closed;

    public override long Size
    {
        get
        {
            lock (gate)
            {
                try
                {
                    EnsureOpen(0);
                    return total ?? -1;
                }
                catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
                {
                    throw new Java.IO.IOException("Could not open audio data.");
                }
            }
        }
    }

    public override int ReadAt(long position, byte[]? buffer, int offset, int size)
    {
        if (buffer == null || position < 0) throw new Java.IO.IOException("Invalid audio position.");
        if (size == 0) return 0;
        lock (gate)
        {
            try
            {
                if (total.HasValue && position >= total.Value) return -1;
                long index = position - windowStart;
                if (index < 0 || index >= windowLength)
                {
                    if (input == null || index != windowLength)
                    {
                        input?.Dispose();
                        input = null;
                        EnsureOpen(position);
                    }
                    windowStart = position;
                    windowLength = input!.Read(window.AsMemory(), stop.Token);
                    index = 0;
                    if (windowLength == 0) return -1;
                }
                int count = Math.Min(size, windowLength - (int)index);
                window.AsSpan((int)index, count).CopyTo(buffer.AsSpan(offset, count));
                return count;
            }
            catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
            {
                throw new Java.IO.IOException("Could not read audio data.");
            }
        }
    }

    private void EnsureOpen(long offset)
    {
        if (Volatile.Read(ref closed) != 0) throw new IOException("Audio source is closed.");
        input ??= AndroidMediaInput.Open(address, offset, null, false, stop.Token);
        total = input.Total ?? total;
    }

    public override void Close()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel();
        lock (gate)
        {
            input?.Dispose();
            input = null;
            stop.Dispose();
        }
    }
}
