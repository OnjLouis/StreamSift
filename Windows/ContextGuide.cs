namespace StreamSift;

internal static class ContextGuide
{
    internal const string ReviewText = "This is the full line from the list. It cannot be edited.\r\n\r\n" +
        "Arrow keys: Read through the text.\r\nShift with arrow keys: Select text.\r\n" +
        "Ctrl+C: Copy the selection.\r\nEscape: Return to the list.";
    internal const string ReviewClose = "Close returns you to the same list row without changing or copying it.\r\n\r\n" +
        "Space or Escape: Close the review.";

    internal static string Streams(int selected)
    {
        var lines = new List<string>
        {
            selected > 1 ? "Multiple streams are selected. Select just one to play it or open its address in a browser."
                : "Move through the results with Up and Down. Each row shows the station and any available format, bitrate and listener count.",
            ""
        };
        if (selected == 1) lines.AddRange(["Enter: Play the stream.", "Alt+Enter: Open its address in the browser."]);
        lines.AddRange(["F4: Review the full current line.", "Ctrl+C: Copy the selected stream addresses.",
            "Shift+Enter: Save the selection as a playlist and open it.", "Alt+D: Go to Stream Details.", "",
            "Ctrl+F: Find in results.", "Ctrl+L: Filter and sort.", "F5: Refresh results."]);
        return string.Join("\r\n", lines);
    }

    internal static string Detail(DetailRow row, bool canSearch)
    {
        var description = row.Kind switch
        {
            DetailKind.StreamAddress => "This is the address of the verified stream.",
            DetailKind.FoundOn => row.FoundOnIsStream
                ? "The stream was found at its own address, not on a separate page."
                : "This is the page or listing where the stream was found.",
            DetailKind.Station => "This is the station name shown in the results.",
            DetailKind.Format => "This is the reported audio format. HLS is a live playlist format.",
            DetailKind.Bitrate => "This is the reported bitrate in kilobits per second. Not all streams report it.",
            DetailKind.Listeners => "This is the listener count reported when the stream was checked. F5 refreshes it if the server reports a new count.",
            _ => "Stream Details shows the first selected stream. Playlist and copy commands in Streams still use the full selection."
        };
        var lines = new List<string> { description, "" };
        if (canSearch)
        {
            var enter = row.EnterAction switch
            {
                DetailAction.SavePlaylist => "Enter: Open Save Stream Playlist for this one HLS stream.",
                DetailAction.ScanServer => "Enter: Scan the server root instead of searching this same stream again.",
                DetailAction.Search when row.Kind == DetailKind.FoundOn => "Enter: Search the source page.",
                DetailAction.Search => "Enter: Search this exact stream address.",
                _ => null
            };
            if (enter != null) lines.Add(enter);
            if (row.Kind == DetailKind.StreamAddress && row.Server != null)
                lines.Add("Shift+Enter: Scan the stream server by its scheme, host and port.");
        }
        if (row.Address != null) lines.Add("Alt+Enter: Open this address in the browser.");
        lines.Add("F4: Review the full line.");
        lines.Add(row.Address == null ? "Ctrl+C: Copy the value." : "Ctrl+C: Copy the address.");
        return string.Join("\r\n", lines);
    }
}
