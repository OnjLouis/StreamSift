import AppKit
import Carbon.HIToolbox

struct DetailRow: Equatable {
    enum Kind { case streamAddress, foundOn, station, format, bitrate, listeners, selection }
    let kind: Kind
    let label: String
    let value: String
    var playlistStation: Station? = nil
    var foundOnIsStream = false
    var text: String { label + ": " + value }

    var address: URL? {
        switch kind {
        case .streamAddress, .foundOn: return StreamPreview.address(value)
        default: return nil
        }
    }

    var server: URL? {
        guard kind == .streamAddress || foundOnIsStream, let address,
              var parts = URLComponents(url: address, resolvingAgainstBaseURL: false) else { return nil }
        parts.user = nil
        parts.password = nil
        parts.path = "/"
        parts.query = nil
        parts.fragment = nil
        return parts.url
    }

    var enterCommand: DetailTable.Command? {
        guard let address else { return nil }
        if playlistStation != nil { return .savePlaylist }
        guard foundOnIsStream else { return .search }
        guard let server, server.absoluteString != address.absoluteString else { return nil }
        return .scanServer
    }

    static func rows(for station: Station?, selectedCount: Int) -> [DetailRow] {
        guard let station else { return [] }
        var rows = [
            DetailRow(kind: .streamAddress, label: "Stream address", value: station.url,
                      playlistStation: station.format == "HLS" ? station : nil),
            DetailRow(kind: .foundOn, label: "Found on", value: station.foundOn,
                      foundOnIsStream: station.foundOn == station.url),
            DetailRow(kind: .station, label: "Station", value: station.name),
            DetailRow(kind: .format, label: "Format", value: station.format),
            DetailRow(kind: .bitrate, label: "Bitrate", value: station.bitrate.map { "\($0) kbps" } ?? "not reported")
        ]
        if let count = station.listeners {
            rows.append(DetailRow(kind: .listeners, label: "Listeners at scan", value: String(count)))
        }
        if selectedCount > 1 {
            rows.append(DetailRow(kind: .selection, label: "Selection", value: "Details for the first of \(selectedCount) streams"))
        }
        return rows
    }
}

final class DetailTable: NSTableView, NSTableViewDataSource, NSTableViewDelegate {
    enum Command { case search, openBrowser, copy, scanServer, savePlaylist, review }
    private(set) var rows: [DetailRow] = []
    var canSearch = true
    var onCommand: ((Command, DetailRow) -> Void)?
    var actionMenu: (() -> NSMenu?)?
    var selectedDetail: DetailRow? { rows.indices.contains(selectedRow) ? rows[selectedRow] : nil }
    override var acceptsFirstResponder: Bool { !rows.isEmpty && super.acceptsFirstResponder }

    func configure() {
        headerView = nil
        usesAlternatingRowBackgroundColors = true
        allowsMultipleSelection = false
        allowsEmptySelection = false
        columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        let column = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("Detail"))
        column.title = "Detail"
        column.width = 630
        column.resizingMask = .autoresizingMask
        addTableColumn(column)
        dataSource = self
        delegate = self
        target = self
        doubleAction = #selector(activateSelected)
    }

    func showRows(_ next: [DetailRow]) {
        guard rows != next else { return }
        let previous = selectedDetail?.kind
        rows = next
        reloadData()
        if !rows.isEmpty {
            let index = rows.firstIndex { $0.kind == previous } ?? 0
            selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
            scrollRowToVisible(index)
        }
    }

    func numberOfRows(in tableView: NSTableView) -> Int { rows.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard rows.indices.contains(row) else { return nil }
        let text = rows[row].text
        let cell = NSTextField(labelWithString: text)
        cell.lineBreakMode = .byTruncatingMiddle
        cell.toolTip = text
        return cell
    }

    @objc func copy(_ sender: Any?) {
        if let selectedDetail { onCommand?(.copy, selectedDetail) }
    }

    @objc private func activateSelected() {
        if canSearch, let selectedDetail, let command = selectedDetail.enterCommand { onCommand?(command, selectedDetail) }
    }

    override func keyDown(with event: NSEvent) {
        let flags = event.modifierFlags.intersection([.control, .option, .shift, .command])
        if flags.isEmpty, event.keyCode == kVK_F4, let selectedDetail {
            onCommand?(.review, selectedDetail)
            return
        }
        if event.keyCode == kVK_Return || event.keyCode == kVK_ANSI_KeypadEnter {
            if flags.isEmpty { activateSelected(); return }
            if flags == .shift {
                if canSearch, let selectedDetail, selectedDetail.kind == .streamAddress, selectedDetail.server != nil {
                    onCommand?(.scanServer, selectedDetail)
                }
                return
            }
            if flags == .option, let selectedDetail, selectedDetail.address != nil {
                onCommand?(.openBrowser, selectedDetail)
                return
            }
        }
        super.keyDown(with: event)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let index = row(at: convert(event.locationInWindow, from: nil))
        guard rows.indices.contains(index) else { return nil }
        if index != selectedRow { selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false) }
        return actionMenu?()
    }

    override func accessibilityPerformShowMenu() -> Bool {
        guard selectedRow >= 0, let menu = actionMenu?() else { return false }
        let cell = rect(ofRow: selectedRow)
        menu.popUp(positioning: nil, at: NSPoint(x: cell.minX + 4, y: cell.maxY), in: self)
        return true
    }
}
