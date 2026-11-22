enum ContextGuide {
    static let reviewText = """
    This is the full line from the list. It cannot be edited.

    Arrow keys: Read through the text.
    Shift with arrow keys: Select text.
    Command+C: Copy the selection.
    Escape: Return to the list.
    """
    static let reviewClose = """
    Close returns you to the same list row without changing or copying it.

    Space or Escape: Close the review.
    """

    static func streams(selected: Int) -> String {
        var lines = [
            selected > 1 ? "Multiple streams are selected. Select just one to play it or open its address in a browser."
                : "Move through the results with Up and Down. Each row shows the station and any available format, bitrate and listener count.",
            ""
        ]
        if selected == 1 { lines += ["Return: Play the stream.", "Option+Return: Open its address in the browser."] }
        lines += ["F4: Review the full current line.", "Command+C: Copy the selected stream addresses.",
                  "Shift+Return: Save the selection as a playlist and open it.", "Command+3: Go to Stream Details.", "",
                  "Command+F: Find in results.", "Command+L: Filter and sort.", "Command+R: Refresh results."]
        return lines.joined(separator: "\n")
    }

    static func detail(_ row: DetailRow, canSearch: Bool) -> String {
        let description: String
        switch row.kind {
        case .streamAddress: description = "This is the address of the verified stream."
        case .foundOn:
            description = row.foundOnIsStream ? "The stream was found at its own address, not on a separate page."
                : "This is the page or listing where the stream was found."
        case .station: description = "This is the station name shown in the results."
        case .format: description = "This is the reported audio format. HLS is a live playlist format."
        case .bitrate: description = "This is the reported bitrate in kilobits per second. Not all streams report it."
        case .listeners:
            description = "This is the listener count reported when the stream was checked. Command+R refreshes it if the server reports a new count."
        case .selection:
            description = "Stream Details shows the first selected stream. Playlist and copy commands in Streams still use the full selection."
        }
        var lines = [description, ""]
        if canSearch {
            switch row.enterCommand {
            case .savePlaylist: lines.append("Return: Open Save Stream Playlist for this one HLS stream.")
            case .scanServer: lines.append("Return: Scan the server root instead of searching this same stream again.")
            case .search:
                lines.append(row.kind == .foundOn ? "Return: Search the source page." : "Return: Search this exact stream address.")
            default: break
            }
            if row.kind == .streamAddress, row.server != nil {
                lines.append("Shift+Return: Scan the stream server by its scheme, host and port.")
            }
        }
        if row.address != nil { lines.append("Option+Return: Open this address in the browser.") }
        lines.append("F4: Review the full line.")
        lines.append(row.address == nil ? "Command+C: Copy the value." : "Command+C: Copy the address.")
        return lines.joined(separator: "\n")
    }
}
