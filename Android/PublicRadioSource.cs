using AndroidX.Media3.DataSource;

namespace StreamSift.Mobile;

internal sealed class PublicRadioSource(CancellationToken lifetime) : Java.Lang.Object, IDataSource
{
    private readonly List<ITransferListener> listeners = [];
    private DataSpec? currentRequest;
    private AndroidMediaInput? input;
    private Android.Net.Uri? address;

    public Android.Net.Uri? Uri => address;

    public IDictionary<string, IList<string>> ResponseHeaders
    {
        get
        {
            var headers = new Android.Runtime.JavaDictionary<string, IList<string>>();
            if (input is { } opened)
            {
                foreach (var header in opened.Headers)
                    headers[header.Key.ToLowerInvariant()] = new Android.Runtime.JavaList<string>(header.Value);
            }
            return headers;
        }
    }

    public void AddTransferListener(ITransferListener? listener)
    {
        if (listener != null && !listeners.Contains(listener)) listeners.Add(listener);
    }

    public long Open(DataSpec? dataSpec)
    {
        if (dataSpec == null) throw new Java.IO.IOException("Audio request is missing.");
        foreach (var listener in listeners) listener.OnTransferInitializing(this, dataSpec, true);
        try
        {
            if (!System.Uri.TryCreate(dataSpec.Uri?.ToString(), UriKind.Absolute, out var url))
                throw new IOException("Invalid audio address.");
            long? length = dataSpec.Length < 0 ? null : dataSpec.Length;
            bool icy = dataSpec.HttpRequestHeaders?["Icy-MetaData"]?.ToString() == "1";
            input = AndroidMediaInput.Open(url, dataSpec.Position, length, icy, lifetime);
            address = Android.Net.Uri.Parse(input.Address.AbsoluteUri);
            currentRequest = dataSpec;
            foreach (var listener in listeners) listener.OnTransferStart(this, dataSpec, true);
            return length ?? input.Length ?? -1;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            throw new Java.IO.IOException("Could not open audio data.");
        }
    }

    public int Read(byte[]? buffer, int offset, int length)
    {
        if (buffer == null) throw new Java.IO.IOException("Audio buffer is missing.");
        if (length == 0) return 0;
        try
        {
            int count = input!.Read(buffer.AsMemory(offset, length), lifetime);
            if (count == 0) return -1;
            foreach (var listener in listeners) listener.OnBytesTransferred(this, currentRequest!, true, count);
            return count;
        }
        catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException)
        {
            throw new Java.IO.IOException("Could not read audio data.");
        }
    }

    public void Close()
    {
        if (input == null) return;
        input.Dispose();
        input = null;
        address?.Dispose();
        address = null;
        if (currentRequest is { } finished)
        {
            currentRequest = null;
            foreach (var listener in listeners) listener.OnTransferEnd(this, finished, true);
        }
    }

    internal sealed class Factory(CancellationToken lifetime) : Java.Lang.Object, IDataSourceFactory
    {
        public IDataSource CreateDataSource() => new PublicRadioSource(lifetime);
    }
}
