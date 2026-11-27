using Android.Media;
using NLayer;

namespace StreamSift.Mobile;

internal sealed class Mp2Output(Uri address, float volume, AndroidPlayback owner) : IDisposable
{
    private static readonly TimeSpan NetworkLimit = TimeSpan.FromSeconds(8);
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private AudioTrack? output;
    private float level = volume;
    private bool disposed;

    internal float Volume
    {
        set
        {
            lock (gate)
            {
                level = value;
                output?.SetVolume(value);
            }
        }
    }

    internal void Start() => _ = Task.Run(DecodeAsync);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            stop.Cancel();
            output?.Pause();
        }
    }

    private async Task DecodeAsync()
    {
        try
        {
            using var client = PublicNetwork.CreateClient();
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            headers.CancelAfter(NetworkLimit);
            using var response = await PublicMediaResponse.OpenAsync(client, address, headers.Token, requestStreamTitle: true).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
            using var input = new LiveAudioStream(stream, stop.Token, LiveAudioStream.MetadataInterval(response),
                title => owner.Mp2Title(this, title));
            using var decoder = new MpegFile(input);
            Render(decoder, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception)
        {
            if (!stop.IsCancellationRequested) owner.Mp2Failed(this);
        }
        finally
        {
            lock (gate)
            {
                disposed = true;
                stop.Dispose();
            }
        }
    }

    private void Render(MpegFile source, CancellationToken token)
    {
        var channels = source.Channels switch
        {
            1 => ChannelOut.Mono,
            2 => ChannelOut.Stereo,
            _ => throw new InvalidDataException("Unsupported MPEG channel count.")
        };
        int rate = source.SampleRate;
        int minimum = AudioTrack.GetMinBufferSize(rate, channels, Encoding.PcmFloat);
        if (minimum <= 0) throw new IOException("Audio output is unavailable.");
        var format = new AudioFormat.Builder().SetSampleRate(rate)!.SetChannelMask(channels)!
            .SetEncoding(Encoding.PcmFloat)!.Build()!;
        var attributes = new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Music)!.Build()!;
        using var device = new AudioTrack.Builder().SetAudioFormat(format)!.SetAudioAttributes(attributes)!
            .SetBufferSizeInBytes(Math.Max(minimum, rate * source.Channels))!.SetTransferMode(AudioTrackMode.Stream)!
            .Build()!;
        var samples = new float[rate * source.Channels / 10];
        try
        {
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                output = device;
                device.SetVolume(level);
                device.Play();
            }
            bool notified = false;
            while (!token.IsCancellationRequested)
            {
                int read = source.ReadSamples(samples, 0, samples.Length);
                if (read == 0) throw new EndOfStreamException();
                int offset = 0;
                while (offset < read)
                {
                    token.ThrowIfCancellationRequested();
                    int written = device.Write(samples, offset, read - offset, WriteMode.NonBlocking);
                    if (written < 0) throw new IOException("Audio output stopped.");
                    offset += written;
                    if (written == 0) Thread.Sleep(10);
                }
                if (notified) continue;
                notified = true;
                owner.Mp2Started(this);
            }
        }
        finally
        {
            lock (gate)
            {
                output = null;
                device.Release();
            }
        }
    }
}
