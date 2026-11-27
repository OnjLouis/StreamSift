namespace StreamSift.Mobile;

internal static class TrackDownload
{
    internal static async Task<string> SaveAsync(Station row, string directory, CancellationToken token)
    {
        using var client = PublicNetwork.CreateClient();
        return await SaveAsync(client, row, directory, token).ConfigureAwait(false);
    }

    internal static async Task<string> SaveAsync(HttpClient client, Station row, string directory, CancellationToken token)
    {
        if (!row.IsTrack) throw new ArgumentException("Select a file to download.", nameof(row));
        using var response = await PublicMediaResponse.OpenAsync(client, row.Url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (type?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) != true &&
            type is not ("application/octet-stream" or "application/ogg"))
            throw new InvalidDataException("The server did not return an audio file.");
        const long maxDownloadBytes = 512L * 1024 * 1024;
        if (response.Content.Headers.ContentLength is not long expectedBytes || expectedBytes <= 0)
            throw new InvalidDataException("This address does not report a finite file size.");
        if (expectedBytes > maxDownloadBytes)
            throw new InvalidDataException("This file exceeds the 512 MiB download limit.");

        string name = Path.GetFileName(row.Url.LocalPath);
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") name = "Audio download";
        string file = Path.Combine(directory, name);
        const int bufferSize = 65536;
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var target = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: bufferSize, useAsync: true);
        try
        {
            await using (target.ConfigureAwait(false))
            {
                byte[] buffer = new byte[bufferSize];
                long remaining = expectedBytes;
                while (true)
                {
                    int read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (read > remaining)
                        throw new InvalidDataException("The server sent more audio data than it reported.");
                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    remaining -= read;
                }
                if (remaining != 0)
                    throw new InvalidDataException("The server sent less audio data than it reported.");
            }
            return file;
        }
        catch
        {
            try { File.Delete(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
