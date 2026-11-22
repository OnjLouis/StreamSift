using System.Globalization;

namespace StreamSift;

internal enum ResultOrder { Search, NameAscending, NameDescending, BitrateDescending, BitrateAscending, ListenersDescending, ListenersAscending }

internal readonly record struct ResultView(string? Format = null, ResultOrder Order = ResultOrder.Search)
{
    private static readonly StringComparer Names = StringComparer.InvariantCultureIgnoreCase;

    internal static string[] Formats(IReadOnlyList<Station> stations) =>
        stations.Select(s => s.Format).Distinct(Names).OrderBy(format => format, Names).ToArray();

    internal ResultView RetainAvailable(IReadOnlyList<Station> stations)
    {
        var format = Format;
        var order = Order;
        if (format != null && !stations.Any(s => Names.Equals(s.Format, format))) format = null;
        if (order is ResultOrder.ListenersAscending or ResultOrder.ListenersDescending && !stations.Any(s => s.Listeners.HasValue))
            order = ResultOrder.Search;
        return new(format, order);
    }

    internal Station[] Apply(IReadOnlyList<Station> stations)
    {
        var format = Format;
        var visible = stations.Where(s => format == null || Names.Equals(s.Format, format));
        return (Order switch
        {
            ResultOrder.NameAscending => visible.OrderBy(s => s.Name, Names),
            ResultOrder.NameDescending => visible.OrderByDescending(s => s.Name, Names),
            ResultOrder.BitrateDescending => visible.OrderBy(s => !s.Bitrate.HasValue).ThenByDescending(s => s.Bitrate),
            ResultOrder.BitrateAscending => visible.OrderBy(s => !s.Bitrate.HasValue).ThenBy(s => s.Bitrate),
            ResultOrder.ListenersDescending => visible.OrderBy(s => !s.Listeners.HasValue).ThenByDescending(s => s.Listeners),
            ResultOrder.ListenersAscending => visible.OrderBy(s => !s.Listeners.HasValue).ThenBy(s => s.Listeners),
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
                (station.Bitrate is int bitrate && Matches($"{bitrate} kbps")) ||
                (station.Listeners is int listeners && Matches(ListenerLabel(listeners)))) return index;
        }
        return -1;

        bool Matches(string value) => CultureInfo.InvariantCulture.CompareInfo.IndexOf(value, query,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }

    internal static string ListenerLabel(int listeners) => listeners == 1 ? "1 listener" : $"{listeners} listeners";
}
