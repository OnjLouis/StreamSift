using StreamSift.Mobile;

namespace StreamSift;

internal static class TrackDownloadChecks
{
    internal static async Task<int> RunAsync()
    {
        var address = new Uri("https://audio.example/track.mp3");
        var row = new Station("Track", address, "MP3", null, address, false, IsTrack: true);
        var directory = Directory.CreateTempSubdirectory("StreamSift-TrackDownloadChecks-");
        try
        {
            string valid = await SaveAsync(3);
            if (!File.ReadAllBytes(valid).SequenceEqual(new byte[] { 1, 2, 3 }))
                throw new InvalidOperationException("A finite audio download should retain the exact bytes.");
            File.Delete(valid);

            await RejectAsync(long.MaxValue, "an oversized declared length");
            await RejectAsync(2, "a response longer than its declared length");
            await RejectAsync(4, "a response shorter than its declared length");
            using var cancellation = new CancellationTokenSource();
            using var client = new HttpClient(new AudioResponse(3, new CancelDuringCopy(cancellation)));
            try
            {
                await TrackDownload.SaveAsync(client, row, directory.FullName, cancellation.Token);
                throw new InvalidOperationException("An interrupted download should be canceled.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (directory.EnumerateFileSystemInfos().Any())
                    throw new InvalidOperationException("A canceled download left a partial file.");
            }
            return 5;
        }
        finally { directory.Delete(recursive: true); }

        async Task<string> SaveAsync(long declaredLength)
        {
            using var client = new HttpClient(new AudioResponse(declaredLength));
            return await TrackDownload.SaveAsync(client, row, directory.FullName, CancellationToken.None);
        }

        async Task RejectAsync(long declaredLength, string reason)
        {
            try { await SaveAsync(declaredLength); }
            catch (InvalidDataException)
            {
                if (directory.EnumerateFileSystemInfos().Any())
                    throw new InvalidOperationException("A failed download left a partial file.");
                return;
            }
            throw new InvalidOperationException("Failed to reject " + reason + ".");
        }
    }

    private sealed class CancelDuringCopy(CancellationTokenSource cancellation) : MemoryStream([1, 2, 3])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (Position != 0) cancellation.Cancel();
            return base.ReadAsync(buffer[..1], token);
        }
    }

    private sealed class AudioResponse(long declaredLength, Stream? stream = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var content = new StreamContent(stream ?? new MemoryStream([1, 2, 3]));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
            content.Headers.ContentLength = declaredLength;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
}
