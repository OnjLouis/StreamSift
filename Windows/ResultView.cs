using System.Globalization;

namespace StreamSift;

internal enum ResultOrder
{
    Search, NameAscending, NameDescending, BitrateDescending, BitrateAscending, ListenersDescending, ListenersAscending,
    DateModifiedDescending, DateModifiedAscending, FileSizeDescending, FileSizeAscending
}

internal readonly record struct ResultView(string? Format = null, ResultOrder Order = ResultOrder.Search)
{
    private static readonly StringComparer Names = StringComparer.InvariantCultureIgnoreCase;
#if STREAMSIFT_WINDOWS7
    private static readonly IComparer<string> FileNames = Comparer<string>.Create(StrCmpLogicalW);

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string? left, string? right);
#else
    private static readonly StringComparer FileNames = StringComparer.Create(CultureInfo.CurrentCulture,
        CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
#endif

    internal static ResultView ForSearch(bool fileIndex, bool preferListedOrder = false) =>
        new(Order: fileIndex && !preferListedOrder ? ResultOrder.NameAscending : ResultOrder.Search);

    internal static string[] Formats(IReadOnlyList<Station> stations) =>
        stations.Select(s => s.Format).Distinct(Names).OrderBy(format => format, Names).ToArray();

    internal static ResultOrder[] AvailableOrders(IReadOnlyList<Station> stations)
    {
        var orders = new List<ResultOrder> { ResultOrder.Search, ResultOrder.NameAscending, ResultOrder.NameDescending };
        if (stations.Any(s => s.IsTrack))
        {
            if (stations.Any(s => s.DateModified.HasValue)) orders.AddRange([ResultOrder.DateModifiedDescending, ResultOrder.DateModifiedAscending]);
            if (stations.Any(s => s.FileSizeBytes.HasValue)) orders.AddRange([ResultOrder.FileSizeDescending, ResultOrder.FileSizeAscending]);
        }
        else
        {
            orders.AddRange([ResultOrder.BitrateDescending, ResultOrder.BitrateAscending]);
            if (stations.Any(s => s.Listeners.HasValue)) orders.AddRange([ResultOrder.ListenersDescending, ResultOrder.ListenersAscending]);
        }
        return orders.ToArray();
    }

    internal ResultView RetainAvailable(IReadOnlyList<Station> stations, bool fileIndex = false)
    {
        var format = Format;
        var order = Order;
        if (format != null && !stations.Any(s => Names.Equals(s.Format, format))) format = null;
        if (!AvailableOrders(stations).Contains(order)) order = ForSearch(fileIndex).Order;
        return new(format, order);
    }

    internal static string OrderLabel(ResultOrder order, bool tracks) => order switch
    {
        ResultOrder.Search => tracks ? "Listed order" : "Search order",
        ResultOrder.NameAscending => tracks ? "Track name, A to Z" : "Station name, A to Z",
        ResultOrder.NameDescending => tracks ? "Track name, Z to A" : "Station name, Z to A",
        ResultOrder.BitrateDescending => "Bitrate, highest first",
        ResultOrder.BitrateAscending => "Bitrate, lowest first",
        ResultOrder.ListenersDescending => "Listeners, most first",
        ResultOrder.ListenersAscending => "Listeners, fewest first",
        ResultOrder.DateModifiedDescending => "Date modified, newest first",
        ResultOrder.DateModifiedAscending => "Date modified, oldest first",
        ResultOrder.FileSizeDescending => "File size, largest first",
        ResultOrder.FileSizeAscending => "File size, smallest first",
        _ => throw new ArgumentOutOfRangeException(nameof(order))
    };

    internal Station[] Apply(IReadOnlyList<Station> stations, IComparer<string>? fileNames = null)
    {
        var format = Format;
        var visible = stations.Where(s => format == null || Names.Equals(s.Format, format));
        var names = stations.Any(s => s.IsTrack) ? fileNames ?? FileNames : Names;
        return (Order switch
        {
            ResultOrder.NameAscending => visible.OrderBy(s => s.Name, names),
            ResultOrder.NameDescending => visible.OrderByDescending(s => s.Name, names),
            ResultOrder.BitrateDescending => visible.OrderBy(s => !s.Bitrate.HasValue).ThenByDescending(s => s.Bitrate),
            ResultOrder.BitrateAscending => visible.OrderBy(s => !s.Bitrate.HasValue).ThenBy(s => s.Bitrate),
            ResultOrder.ListenersDescending => visible.OrderBy(s => !s.Listeners.HasValue).ThenByDescending(s => s.Listeners),
            ResultOrder.ListenersAscending => visible.OrderBy(s => !s.Listeners.HasValue).ThenBy(s => s.Listeners),
            ResultOrder.DateModifiedDescending => visible.OrderBy(s => !s.DateModified.HasValue).ThenByDescending(s => s.DateModified),
            ResultOrder.DateModifiedAscending => visible.OrderBy(s => !s.DateModified.HasValue).ThenBy(s => s.DateModified),
            ResultOrder.FileSizeDescending => visible.OrderBy(s => !s.FileSizeBytes.HasValue).ThenByDescending(s => s.FileSizeBytes),
            ResultOrder.FileSizeAscending => visible.OrderBy(s => !s.FileSizeBytes.HasValue).ThenBy(s => s.FileSizeBytes),
            _ => visible
        }).ToArray();
    }

    internal static int Find(IReadOnlyList<Station> stations, int anchor, string query, bool backwards)
    {
        query = query.Trim();
        if (query.Length == 0 || stations.Count == 0) return -1;
        int size = stations.Count;
        int start = anchor >= 0 && anchor < size ? anchor : backwards ? 0 : size - 1;
        for (int step = 1; step <= size; step++)
        {
            int index = (start + (backwards ? -step : step) + size) % size;
            Station station = stations[index];
            if (Matches(station.Name) || Matches(station.Format) || Matches(station.Url.AbsoluteUri) ||
                (station.ScanTitle is string title && Matches(title)) ||
                (station.Bitrate is int bitrate && Matches($"{bitrate} kbps")) ||
                (station.Listeners is int listeners && Matches(ListenerLabel(listeners)))) return index;
        }
        return -1;

        bool Matches(string value) => CultureInfo.InvariantCulture.CompareInfo.IndexOf(value, query,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    internal static string ListenerLabel(int listeners) => listeners == 1 ? "1 listener" : $"{listeners} listeners";
}
