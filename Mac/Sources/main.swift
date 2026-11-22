import AppKit
import Darwin
import UniformTypeIdentifiers

@MainActor final class StreamSiftApp: NSObject, NSApplicationDelegate, NSWindowDelegate, NSComboBoxDelegate,
                                     NSTableViewDataSource, NSTableViewDelegate, NSMenuItemValidation {
    private let projectURL = URL(string: "https://github.com/OnjLouis/StreamSift")!
    private let window = KeyboardWindow(contentRect: NSRect(x: 0, y: 0, width: 730, height: 505),
                                        styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                        backing: .buffered, defer: false)
    private let address = NSComboBox()
    private let find = NSButton(title: "Find Streams", target: nil, action: nil)
    private let findBrowser = NSButton(title: "Find with Browser", target: nil, action: nil)
    private let cancel = NSButton(title: "Cancel", target: nil, action: nil)
    private let table = StreamTable()
    private let details = DetailTable()
    private let status = readOnlyText("Status", hint: "Read the latest search or action result.")
    private let defaults = UserDefaults.standard
    private lazy var history = SearchHistory(reading: defaults)
    private var historyOpen = false
    private var settings: PreferencesWindow?
    private var preferences = Preferences()
    private var startSound: NSSound?
    private var doneSound: NSSound?
    private var stations: [Station] = []
    private var allStations: [Station] = []
    private var resultView = ResultView()
    private var resultQuery = ""
    private var lastSearch: (input: String, mode: SearchMode)?
    private var names: [String] = []
    private var job: ScanJob?
    private var isCancelling = false
    private var checkingUpdate = false
    private var deferredOffer = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        preferences = Preferences(reading: defaults)
        _ = try? PlaybackHandoff.prune()
        startSound = sound(named: "search")
        doneSound = sound(named: "done")
        buildMenus()
        buildWindow()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(address)
        if preferences.startupUpdates { checkForUpdates(automatic: true) }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) { job?.cancel() }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        window.makeKeyAndOrderFront(nil)
        return true
    }

    private func sound(named name: String) -> NSSound? {
        Bundle.main.url(forResource: name, withExtension: "wav").flatMap { NSSound(contentsOf: $0, byReference: true) }
    }

    private func buildWindow() {
        window.title = "StreamSift"
        window.delegate = self
        window.minSize = NSSize(width: 530, height: 385)
        window.isReleasedWhenClosed = false
        window.help = { [weak self] in self?.focusedHelp() }
        window.updates = { [weak self] in self?.checkUpdates() }
        window.project = { [weak self] in self?.openProject() }
        window.findResult = { [weak self] backwards in
            guard let self, job == nil, !stations.isEmpty, window.attachedSheet == nil else { return false }
            findResult(backwards: backwards)
            return true
        }
        window.keyOrder = { [weak self] in self?.mainKeyOrder ?? [] }
        window.cancel = { [weak self] in
            guard self?.job != nil else { return false }
            self?.cancelSearch()
            return true
        }
        let caption = NSTextField(labelWithString: "Website address or station name:")
        caption.setAccessibilityServesAsTitleForUIElements([address])
        address.isBezeled = true
        address.target = self
        address.action = #selector(findStreams)
        address.delegate = self
        address.numberOfVisibleItems = SearchHistory.capacity
        address.completes = false
        refreshHistory()
        address.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        label("Website address or station name", for: address,
              hint: "Enter a public website, stream address, station name or call sign, or choose a recent successful search from the list.")
        find.target = self
        find.action = #selector(findStreams)
        findBrowser.target = self
        findBrowser.action = #selector(findWithBrowser)
        cancel.target = self
        cancel.action = #selector(cancelSearch)
        label("Find Streams", for: find, hint: "Search for live audio without starting a web player.")
        label("Find with Browser", for: findBrowser, hint: "Look for live audio using a temporary, muted browser. Requires Chrome or Microsoft Edge.")
        label("Cancel", for: cancel, hint: "Stop the current search. You can also press Escape.")
        let entry = NSStackView(views: [caption, address])
        entry.alignment = .centerY
        let searchButtons = NSStackView(views: [find, findBrowser, cancel])
        searchButtons.alignment = .centerY

        label("Streams", for: table, hint: "Command+A selects all visible streams, and Command+S saves the selection in one playlist. Return plays one selected stream. Command+3 moves to Stream Details.")
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = true
        table.allowsEmptySelection = false
        table.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        let streamColumn = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("Stream"))
        streamColumn.title = "Stream"
        streamColumn.width = 630
        streamColumn.minWidth = 140
        streamColumn.resizingMask = [.autoresizingMask, .userResizingMask]
        table.addTableColumn(streamColumn)
        table.target = self
        table.doubleAction = #selector(previewStream)
        table.copyAddress = { [weak self] in self?.copyAddress() }
        table.openPlayer = { [weak self] in self?.openDefaultPlayer() }
        table.previewStream = { [weak self] in self?.previewStream() }
        table.openBrowser = { [weak self] in self?.openStreamInBrowser() }
        table.didSelectAll = { [weak self] in
            guard let self else { return }
            let count = table.selectedRowIndexes.count
            setStatus(count == 1 ? "1 stream selected." : "\(count) streams selected.")
        }
        table.actionMenu = { [weak self] in self?.resultMenu() }
        table.dataSource = self
        table.delegate = self
        details.configure()
        label("Stream details", for: details,
              hint: "Return searches a selected address. Option+Return opens it in your browser. Command+C copies the row. The context menu can scan the stream server.")
        details.onCommand = { [weak self] command, row in self?.runDetailAction(command, row: row) }
        details.actionMenu = { [weak self] in self?.detailMenu() }
        let listView = scroll(table)
        let detailView = scroll(details)
        let statusView = scroll(status)
        let content = NSStackView(views: [entry, searchButtons, listView, detailView, statusView])
        content.orientation = .vertical
        content.alignment = .leading
        content.spacing = 10
        content.edgeInsets = NSEdgeInsets(top: 14, left: 14, bottom: 14, right: 14)
        window.contentView = content
        for view in [entry, listView, detailView, statusView] {
            view.widthAnchor.constraint(equalTo: content.widthAnchor, constant: -28).isActive = true
        }
        detailView.heightAnchor.constraint(equalToConstant: 118).isActive = true
        statusView.heightAnchor.constraint(equalToConstant: 53).isActive = true
        listView.setContentHuggingPriority(.defaultLow, for: .vertical)
        window.initialFirstResponder = address
        window.center()
        refreshNavigation()
        setStatus("Ready.")
    }

    private func item(_ title: String, _ action: Selector, _ key: String = "",
                      modifiers: NSEvent.ModifierFlags = .command) -> NSMenuItem {
        let result = NSMenuItem(title: title, action: action, keyEquivalent: key)
        result.target = self
        result.keyEquivalentModifierMask = modifiers
        return result
    }

    private func buildMenus() {
        let main = NSMenu()
        func section(_ name: String, _ items: [NSMenuItem]) -> NSMenu {
            let menu = NSMenu(title: name)
            for child in items { menu.addItem(child) }
            let root = NSMenuItem(title: name, action: nil, keyEquivalent: "")
            root.submenu = menu
            main.addItem(root)
            return menu
        }
        _ = section("StreamSift", [
            item("About StreamSift", #selector(about)), .separator(),
            item("Settings...", #selector(showSettings), ","),
            item("Check for Updates...", #selector(checkUpdates), functionOne, modifiers: .shift), .separator(),
            NSMenuItem(title: "Hide StreamSift", action: #selector(NSApplication.hide(_:)), keyEquivalent: "h"),
            NSMenuItem(title: "Hide Others", action: #selector(NSApplication.hideOtherApplications(_:)), keyEquivalent: "h"),
            NSMenuItem(title: "Show All", action: #selector(NSApplication.unhideAllApplications(_:)), keyEquivalent: ""), .separator(),
            NSMenuItem(title: "Quit StreamSift", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        ])
        main.items[0].submenu?.item(withTitle: "Hide Others")?.keyEquivalentModifierMask = [.command, .option]
        _ = section("File", [
            item("New Search", #selector(reset), "n"),
            item("Save Playlist...", #selector(savePlaylist), "s"),
            item("Open Playlist Folder", #selector(openPlaylistFolder), "o", modifiers: [.command, .shift]),
            item("Clear Search History", #selector(clearHistory)),
            .separator(),
            NSMenuItem(title: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        ])
        _ = section("Edit", [
            NSMenuItem(title: "Undo", action: Selector(("undo:")), keyEquivalent: "z"),
            NSMenuItem(title: "Redo", action: Selector(("redo:")), keyEquivalent: "Z"), .separator(),
            NSMenuItem(title: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x"),
            NSMenuItem(title: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c"),
            NSMenuItem(title: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v"),
            NSMenuItem(title: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        ])
        _ = section("Actions", [
            item("Find Streams", #selector(findStreams), "\r"),
            item("Find with Browser", #selector(findWithBrowser), "\r", modifiers: [.command, .shift]),
            item("Look Up in Public Directory", #selector(findInDirectory), "d", modifiers: [.command, .shift]),
            item("Refresh Results", #selector(refreshResults), "r"), .separator(),
            item("Copy Stream Addresses", #selector(copyAddress)),
            item("Play Stream", #selector(previewStream)),
            item("Open in Browser", #selector(openFocusedAddressInBrowser), "\r", modifiers: .option),
            item("Save and Open in Default Player", #selector(openDefaultPlayer)), .separator(),
            item("Go to Address", #selector(focusAddress), "1"),
            item("Go to Streams", #selector(focusStreams), "2"),
            item("Go to Stream Details", #selector(focusDetails), "3"), .separator(),
            item("Filter and Sort...", #selector(showResultFilter), "l"),
            item("Find in Results...", #selector(showResultFind), "f"),
            item("Find Next", #selector(findNextResult), "g"),
            item("Find Previous", #selector(findPreviousResult), "g", modifiers: [.command, .shift])
        ])
        NSApp.windowsMenu = section("Window", [
            NSMenuItem(title: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m"),
            NSMenuItem(title: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        ])
        NSApp.helpMenu = section("Help", [
            item("Context Help", #selector(focusedHelp), functionOne, modifiers: []),
            item("Open Manual", #selector(openManual)),
            item("GitHub Project", #selector(openProject), functionOne),
            item("Donate", #selector(donate))
        ])
        NSApp.mainMenu = main
    }

    private var selectedStations: [Station] {
        table.selectedRowIndexes.compactMap { stations.indices.contains($0) ? stations[$0] : nil }
    }
    private var selected: Station? {
        guard let row = table.selectedRowIndexes.first, stations.indices.contains(row) else { return nil }
        return stations[row]
    }

    @objc private func focusAddress() { if job == nil { window.makeFirstResponder(address) } }
    @objc private func focusStreams() { if !stations.isEmpty { window.makeFirstResponder(table) } }
    @objc private func focusDetails() { if selected != nil { window.makeFirstResponder(details) } }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        switch menuItem.action {
        case #selector(savePlaylist), #selector(copyAddress), #selector(openDefaultPlayer), #selector(focusDetails):
            return selected != nil && window.isKeyWindow && window.attachedSheet == nil
        case #selector(openFocusedAddressInBrowser):
            return (window.focusedView === details ? details.selectedDetail?.address != nil : table.selectedRowIndexes.count == 1) &&
                window.isKeyWindow && window.attachedSheet == nil
        case #selector(previewStream), #selector(openStreamInBrowser):
            return table.selectedRowIndexes.count == 1 && window.isKeyWindow && window.attachedSheet == nil
        case #selector(searchDetail):
            return details.canSearch && details.selectedDetail?.address != nil && window.attachedSheet == nil
        case #selector(scanDetailServer):
            return details.canSearch && details.selectedDetail?.server != nil && window.attachedSheet == nil
        case #selector(openDetail), #selector(copyDetail):
            return details.selectedDetail != nil && window.attachedSheet == nil
        case #selector(focusAddress):
            return job == nil && window.isKeyWindow && window.attachedSheet == nil
        case #selector(focusStreams):
            return !stations.isEmpty && window.isKeyWindow && window.attachedSheet == nil
        case #selector(showResultFilter):
            return job == nil && !allStations.isEmpty && window.isKeyWindow && window.attachedSheet == nil
        case #selector(showResultFind), #selector(findNextResult), #selector(findPreviousResult):
            return job == nil && !stations.isEmpty && window.isKeyWindow && window.attachedSheet == nil
        case #selector(refreshResults):
            return job == nil && lastSearch != nil && !historyOpen && window.isKeyWindow && window.attachedSheet == nil
        case #selector(openPlaylistFolder):
            return window.isKeyWindow && window.attachedSheet == nil
        case #selector(clearHistory):
            return job == nil && !history.entries.isEmpty && window.isKeyWindow && window.attachedSheet == nil
        case #selector(reset), #selector(findStreams), #selector(findWithBrowser), #selector(findInDirectory):
            return job == nil && window.isKeyWindow && window.attachedSheet == nil
        case #selector(checkUpdates): return !checkingUpdate
        default: return true
        }
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.insertNewline(_:)), control === address {
            if historyOpen { return false }
            findStreams()
            return true
        }
        return false
    }

    func comboBoxWillPopUp(_ notification: Notification) { historyOpen = true }
    func comboBoxWillDismiss(_ notification: Notification) { historyOpen = false }

    func numberOfRows(in tableView: NSTableView) -> Int { stations.count }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard stations.indices.contains(row) else { return nil }
        let text = stations[row].listLabel(named: names[row])
        let cell = NSTextField(labelWithString: text)
        cell.lineBreakMode = .byTruncatingMiddle
        cell.toolTip = text
        return cell
    }

    func tableViewSelectionDidChange(_ notification: Notification) {
        details.showRows(DetailRow.rows(for: selected, selectedCount: table.selectedRowIndexes.count))
        refreshNavigation()
    }

    private func refreshNavigation() {
        table.hasStreams = !stations.isEmpty
        table.setAccessibilityElement(table.hasStreams)
        details.setAccessibilityElement(selected != nil)
        details.canSearch = job == nil
        address.isEnabled = job == nil
        find.isEnabled = job == nil
        findBrowser.isEnabled = job == nil
        cancel.isEnabled = job != nil
        let controls = mainKeyOrder
        for (index, view) in controls.enumerated() { view.nextKeyView = controls[(index + 1) % controls.count] }
    }

    private var mainKeyOrder: [NSView] {
        (job == nil ? [address, find, findBrowser] as [NSView] : [cancel]) + (stations.isEmpty ? [] : [table]) +
            (selected == nil ? [] : [details]) + [status]
    }

    private func load(_ found: [Station]) {
        allStations = found
        resultView = ResultView()
        resultQuery = ""
        render(found, preserving: [])
    }

    private func render(_ found: [Station], preserving selection: Set<String>) {
        guard stations != found else { refreshNavigation(); return }
        stations = found
        func group(_ row: Station) -> [String] { [row.name, row.format, String(row.bitrate ?? -1)] }
        let counts = Dictionary(grouping: found, by: group)
        names = found.map { row in
            guard counts[group(row), default: []].count > 1, let url = URL(string: row.url) else { return row.name }
            return "\(row.name) (\(url.host ?? "")\(url.path))"
        }
        table.reloadData()
        if !found.isEmpty {
            let kept = IndexSet(found.indices.filter { selection.contains(found[$0].url) })
            table.selectRowIndexes(kept.isEmpty ? IndexSet(integer: 0) : kept, byExtendingSelection: false)
            table.scrollRowToVisible(kept.first ?? 0)
        }
        tableViewSelectionDidChange(Notification(name: NSTableView.selectionDidChangeNotification))
    }

    @objc private func showResultFilter() {
        guard !allStations.isEmpty, window.attachedSheet == nil else { return }
        let formats = ResultView.formats(allStations)
        let format = NSPopUpButton()
        format.addItems(withTitles: ["All formats"] + formats)
        format.selectItem(at: resultView.format.flatMap { formats.firstIndex(of: $0) }.map { $0 + 1 } ?? 0)
        let sort = NSPopUpButton()
        let orders = allStations.contains { $0.listeners != nil } ? ResultOrder.allCases :
            ResultOrder.allCases.filter { $0 != .listenersDescending && $0 != .listenersAscending }
        sort.addItems(withTitles: orders.map(\.label))
        sort.selectItem(at: resultView.order.rawValue)
        let sheet = ResultCommandSheet(title: "Filter and Sort", action: "Apply", fields: [
            ("Format", format, "Show all formats or a format found in the current search."),
            ("Sort by", sort, "Choose search order, station name, bitrate or listeners where reported. Unknown counts and bitrates appear last.")
        ], manual: { [weak self] in self?.openManual() })
        sheet.present(on: window) { [weak self] in
            guard let self else { return }
            let index = format.indexOfSelectedItem - 1
            resultView = ResultView(format: formats.indices.contains(index) ? formats[index] : nil,
                                    order: ResultOrder(rawValue: sort.indexOfSelectedItem) ?? .search)
            render(resultView.apply(allStations), preserving: Set(selectedStations.map(\.url)))
            setStatus("Showing \(stations.count) of \(allStations.count) streams.")
            window.makeFirstResponder(table)
        }
    }

    @objc private func showResultFind() { promptFind(backwards: false) }
    @objc private func findNextResult() { findResult(backwards: false) }
    @objc private func findPreviousResult() { findResult(backwards: true) }

    private func promptFind(backwards: Bool) {
        guard !stations.isEmpty, window.attachedSheet == nil else { return }
        let input = NSTextField(string: resultQuery)
        let sheet = ResultCommandSheet(title: "Find in Results", action: "Find", fields: [
            ("Find what", input, "Find text in the visible streams by name, format, bitrate, listeners or address.")
        ], manual: { [weak self] in self?.openManual() })
        sheet.present(on: window) { [weak self] in
            guard let self else { return }
            let text = input.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !text.isEmpty else { return }
            resultQuery = String(text.prefix(256))
            findResult(backwards: backwards, fromStart: true)
        }
    }

    private func findResult(backwards: Bool, fromStart: Bool = false) {
        guard !stations.isEmpty else { return }
        if resultQuery.isEmpty { promptFind(backwards: backwards); return }
        let anchor = fromStart ? -1 : table.selectedRow
        guard let index = ResultView.find(in: stations, after: anchor,
                                          query: resultQuery, backwards: backwards) else {
            setStatus("No match in the visible streams.")
            return
        }
        table.selectRowIndexes(IndexSet(integer: index), byExtendingSelection: false)
        table.scrollRowToVisible(index)
        window.makeFirstResponder(table)
        if index == anchor { setStatus("Only one match in the visible streams.") }
    }

    private func setStatus(_ message: String) {
        status.string = message
        status.setSelectedRange(NSRange(location: 0, length: 0))
        if window.isKeyWindow && window.firstResponder !== status {
            NSAccessibility.post(element: window, notification: .announcementRequested, userInfo: [
                .announcement: message, .priority: NSAccessibilityPriorityLevel.medium.rawValue
            ])
        }
    }

    @objc private func findStreams() { if !historyOpen { startSearch(mode: .passive) } }

    @objc private func findWithBrowser() { startSearch(mode: .browser) }

    @objc private func findInDirectory() { startSearch(mode: .directory) }

    @objc private func refreshResults() {
        guard let lastSearch, !historyOpen else { return }
        startSearch(mode: lastSearch.mode, refreshInput: lastSearch.input)
    }

    private func startSearch(mode: SearchMode, refreshInput: String? = nil, newInput: String? = nil) {
        guard job == nil else { return }
        let refreshing = refreshInput != nil
        let input = refreshInput ?? newInput ?? address.currentEditor()?.string ?? address.stringValue
        let running = ScanJob()
        do {
            try running.start(input, mode: mode, fallback: preferences.automaticFallback, onFallback: { [weak self, weak running] next in
                guard let self, job === running, !isCancelling else { return }
                setStatus("Trying another method. " + next.starting)
            }) { [weak self] result in self?.finished(result, mode: mode, input: input, refreshing: refreshing) }
            isCancelling = false
            job = running
            lastSearch = (input, mode)
            if !refreshing { load([]) }
            refreshNavigation()
            window.makeFirstResponder(cancel)
            if refreshing || newInput != nil { address.stringValue = input }
            setStatus(refreshing ? "Refreshing results..." : mode.starting)
            if preferences.sounds { startSound?.stop(); startSound?.play() }
        } catch {
            setStatus(error.localizedDescription)
            window.makeFirstResponder(address)
        }
    }

    @objc private func cancelSearch() {
        guard let job, !isCancelling else { return }
        isCancelling = true
        job.cancel()
    }

    private func finished(_ result: Result<SearchResult, Error>, mode: SearchMode, input: String, refreshing: Bool) {
        let focused = window.focusedView
        let moveFocus = focused === cancel || focused === address || focused === find || focused === findBrowser
        job = nil
        var message: String
        if isCancelling {
            message = refreshing && !stations.isEmpty ? "Refresh cancelled. Previous results kept." : "Search cancelled."
        } else {
            switch result {
            case .success(let found):
                if history.remember(input, verifiedStreams: found.stations.count, in: defaults) { refreshHistory() }
                let previousFormat = resultView.format
                if refreshing {
                    allStations = found.stations
                    resultView = resultView.retainingAvailable(in: allStations)
                    render(resultView.apply(allStations), preserving: Set(selectedStations.map(\.url)))
                } else {
                    load(found.stations)
                }
                let prefix = found.stations.isEmpty && (found.attempts ?? 0) > 1 ? "Automatic search: " : (found.method ?? mode).resultPrefix
                message = (refreshing ? "Refresh finished. " : "") + prefix + found.summary
                if refreshing, let format = resultView.format {
                    message += " Showing \(stations.count) \(format) streams."
                } else if refreshing, let previousFormat, !allStations.isEmpty {
                    message += " \(previousFormat) is no longer available. Showing all formats."
                }
                if preferences.sounds { doneSound?.stop(); doneSound?.play() }
            case .failure(let error):
                message = refreshing && !stations.isEmpty ? "Refresh could not finish. Previous results kept." :
                    ((error as? StreamError)?.errorDescription ?? StreamError.search.errorDescription ?? "Search failed.")
            }
        }
        refreshNavigation()
        let notice = deferredOffer ? " An update is available. Press Shift+F1 to review it." : ""
        deferredOffer = false
        setStatus(message + notice)
        if moveFocus { window.makeFirstResponder(stations.isEmpty ? status : table) }
    }

    private func refreshHistory() {
        let input = address.stringValue
        address.removeAllItems()
        address.addItems(withObjectValues: history.entries)
        address.stringValue = input
    }

    @objc private func clearHistory() {
        guard job == nil else { return }
        history.clear(in: defaults)
        refreshHistory()
        setStatus("Search history cleared.")
    }

    @objc private func reset() {
        guard job == nil else { return }
        lastSearch = nil
        if address.indexOfSelectedItem >= 0 { address.deselectItem(at: address.indexOfSelectedItem) }
        address.stringValue = ""
        load([])
        setStatus("Ready.")
        window.makeFirstResponder(address)
    }

    private func resultMenu() -> NSMenu {
        let menu = NSMenu()
        menu.addItem(item("Play Stream", #selector(previewStream), "\r", modifiers: []))
        menu.addItem(item("Open Stream in Browser", #selector(openStreamInBrowser), "\r", modifiers: .option))
        menu.addItem(item(table.selectedRowIndexes.count == 1 ? "Copy Stream Address" : "Copy Stream Addresses", #selector(copyAddress), "c"))
        menu.addItem(item("Save Playlist...", #selector(savePlaylist), "s"))
        menu.addItem(item("Save and Open in Default Player", #selector(openDefaultPlayer), "\r", modifiers: .shift))
        return menu
    }

    private func detailMenu() -> NSMenu? {
        guard let row = details.selectedDetail else { return nil }
        let menu = NSMenu()
        if row.address != nil {
            menu.addItem(item("Search This Address", #selector(searchDetail), "\r", modifiers: []))
            menu.addItem(item("Open in Browser", #selector(openDetail), "\r", modifiers: .option))
        }
        menu.addItem(item(row.address == nil ? "Copy Value" : "Copy Address", #selector(copyDetail), "c"))
        if row.server != nil { menu.addItem(item("Scan Stream Server", #selector(scanDetailServer))) }
        return menu
    }

    @objc private func searchDetail() {
        if let row = details.selectedDetail { runDetailAction(.search, row: row) }
    }

    @objc private func openDetail() {
        if let row = details.selectedDetail { runDetailAction(.openBrowser, row: row) }
    }

    @objc private func copyDetail() {
        if let row = details.selectedDetail { runDetailAction(.copy, row: row) }
    }

    @objc private func scanDetailServer() {
        if details.canSearch, let server = details.selectedDetail?.server {
            startSearch(mode: .passive, newInput: server.absoluteString)
        }
    }

    private func runDetailAction(_ command: DetailTable.Command, row: DetailRow) {
        switch command {
        case .search:
            if details.canSearch, let target = row.address { startSearch(mode: .passive, newInput: target.absoluteString) }
        case .openBrowser:
            if let target = row.address { openAddressInBrowser(target) }
        case .copy:
            copyText(row.value, confirmation: row.label + " copied.")
        }
    }

    @objc private func copyAddress() {
        let selection = selectedStations
        guard !selection.isEmpty else { return }
        copyText(selection.map(\.url).joined(separator: "\n"),
                 confirmation: selection.count == 1 ? "Stream address copied." : "\(selection.count) stream addresses copied.")
    }

    private func copyText(_ text: String, confirmation: String) {
        NSPasteboard.general.clearContents()
        if NSPasteboard.general.setString(text, forType: .string) { setStatus(confirmation) }
        else { showMessage("Copy Could Not Finish", "The clipboard is unavailable. Try again.") }
    }

    @objc private func savePlaylist() {
        let selection = selectedStations
        guard !selection.isEmpty, window.attachedSheet == nil else { return }
        do { try FileManager.default.createDirectory(at: preferences.folder, withIntermediateDirectories: true) }
        catch { showMessage("Could Not Open Playlist Folder", error.localizedDescription); return }
        let save = NSSavePanel()
        save.title = "Save Stream Playlist"
        save.directoryURL = preferences.folder
        save.allowedContentTypes = [UTType(filenameExtension: "m3u8") ?? .data]
        save.nameFieldStringValue = Playlist.stem(selection) + ".m3u8"
        save.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let file = save.url else { return }
            do {
                try Playlist.data(selection).write(to: file, options: .atomic)
                self?.reportSaved(file, count: selection.count)
            } catch { self?.showMessage("Playlist Could Not Be Saved", error.localizedDescription) }
        }
    }

    @objc private func openPlaylistFolder() {
        let folder = preferences.folder
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            if !NSWorkspace.shared.open(folder) {
                showMessage("Could Not Open Playlist Folder", "Finder could not open the playlist folder.")
            }
        } catch { showMessage("Could Not Open Playlist Folder", error.localizedDescription) }
    }

    @objc private func previewStream() {
        if table.selectedRowIndexes.count > 1 {
            setStatus("Select one stream to play it, or press Shift+Return to save and open the selection.")
            return
        }
        guard table.selectedRowIndexes.count == 1, let selected, window.attachedSheet == nil else { return }
        guard let address = StreamPreview.address(selected) else {
            showMessage("Could Not Play Stream", "The stream does not have a public HTTP or HTTPS address.")
            return
        }
        let workspace = NSWorkspace.shared
        let associated = (preferences.mediaPlayer == .automatic ? UTType(filenameExtension: "m3u8") : nil)
            .flatMap { workspace.urlForApplication(toOpen: $0) }
        guard let (player, target) = StreamPreview.target(choice: preferences.mediaPlayer, associated: associated,
            associatedIdentifier: associated.flatMap { Bundle(url: $0)?.bundleIdentifier },
            installed: { workspace.urlForApplication(withBundleIdentifier: $0) }) else {
            let message = preferences.mediaPlayer == .automatic ? "Install VLC or FastPlay." :
                "\(preferences.mediaPlayer.title) was not found. Choose another media player in Settings."
            showMessage("Play Stream", message + " Shift+Return saves and opens a playlist in your default player.")
            return
        }
        let playback: URL
        do { playback = try player == .fastPlay ? PlaybackHandoff.stage(selected) : address }
        catch { showMessage("Could Not Play Stream", error.localizedDescription); return }
        // An explicit app target keeps an HTTP address out of the browser.
        workspace.open([playback], withApplicationAt: target, configuration: NSWorkspace.OpenConfiguration()) { [weak self] app, error in
            DispatchQueue.main.async {
                if error != nil || app == nil {
                    if player == .fastPlay { try? FileManager.default.removeItem(at: playback) }
                    self?.showMessage("Could Not Play Stream", "\(player.title) could not open the stream. Try saving a playlist instead.")
                } else {
                    self?.setStatus("Sent to \(player.title).")
                    if player == .fastPlay {
                        DispatchQueue.main.asyncAfter(deadline: .now() + PlaybackHandoff.retention + 1) {
                            _ = try? PlaybackHandoff.prune()
                        }
                    }
                }
            }
        }
    }

    @objc private func openFocusedAddressInBrowser() {
        if window.focusedView === details { openDetail(); return }
        openStreamInBrowser()
    }

    @objc private func openStreamInBrowser() {
        guard table.selectedRowIndexes.count == 1, let selected, window.attachedSheet == nil else { return }
        if let address = StreamPreview.address(selected) { openAddressInBrowser(address) }
    }

    private func openAddressInBrowser(_ address: URL) {
        guard NSWorkspace.shared.open(address) else {
            showMessage("Could Not Open Stream", "The stream address could not be opened in your browser.")
            return
        }
        setStatus("Opened in browser.")
    }

    @objc private func openDefaultPlayer() {
        let selection = selectedStations
        guard !selection.isEmpty, window.attachedSheet == nil else { return }
        do {
            let file = try Playlist.saveNew(selection, in: preferences.folder)
            reportSaved(file, count: selection.count)
            if !NSWorkspace.shared.open(file) {
                showMessage("Could Not Open the Playlist", "The playlist was saved at:\n\(file.path)\n\nChoose a default app for .m3u8 files in Finder.")
            }
        } catch { showMessage("Playlist Could Not Be Saved", error.localizedDescription) }
    }

    private func reportSaved(_ file: URL, count: Int) {
        setStatus("Playlist saved: \(file.lastPathComponent)" + (count > 1 ? ". \(count) streams." : ""))
    }

    @objc private func showSettings() {
        if let settings, settings.window.isVisible {
            settings.window.makeKeyAndOrderFront(nil)
            return
        }
        let dialog = PreferencesWindow(current: preferences, manual: { [weak self] in self?.openManual() }) { [weak self] choice in
            guard let self else { return }
            choice.save(to: defaults)
            preferences = choice
            if !choice.startupUpdates { deferredOffer = false }
            setStatus("Settings saved.")
        }
        settings = dialog
        dialog.window.makeKeyAndOrderFront(nil)
    }

    @objc private func openProject() { NSWorkspace.shared.open(projectURL) }
    @objc private func donate() { NSWorkspace.shared.open(URL(string: "https://onj.me/donate")!) }
    @objc private func about() { NSApp.orderFrontStandardAboutPanel(nil) }
    @objc private func openManual() {
        if let manual = Bundle.main.url(forResource: "Manual", withExtension: "html") { NSWorkspace.shared.open(manual) }
    }

    @objc private func focusedHelp() {
        if settings?.window.isKeyWindow == true { settings?.showHelp(); return }
        guard window.attachedSheet == nil else { return }
        let focus = window.focusedView
        let text = focus?.accessibilityHelp().flatMap { $0.isEmpty ? nil : $0 } ??
            "Enter a website address and press Return. When streams are found, use the results table to copy or save a stream."
        HelpPanel(title: focus?.accessibilityLabel() ?? "StreamSift Help", text: text, manual: { [weak self] in self?.openManual() })
            .present(on: window)
    }

    private func showMessage(_ title: String, _ message: String) {
        guard window.attachedSheet == nil else { setStatus(message); return }
        HelpPanel(title: title, text: message, manual: { [weak self] in self?.openManual() }).present(on: window)
    }

    @objc private func checkUpdates() { checkForUpdates(automatic: false) }
    private func checkForUpdates(automatic: Bool) {
        guard !checkingUpdate else { return }
        checkingUpdate = true
        Task {
            defer { checkingUpdate = false }
            do {
                guard let release = try await Updater.latest() else {
                    if !automatic { showMessage("Check for Updates", "StreamSift is up to date.") }
                    return
                }
                if automatic {
                    if preferences.startupUpdates {
                        if job != nil { deferredOffer = true }
                        else { setStatus("An update is available. Press Shift+F1 to review it.") }
                    }
                    return
                }
                guard window.attachedSheet == nil else { return }
                let alert = NSAlert()
                alert.messageText = "StreamSift \(release.version) is available"
                alert.informativeText = String(release.notes.prefix(2000))
                alert.addButton(withTitle: "Install Update")
                alert.addButton(withTitle: "Later")
                if await alert.beginSheetModal(for: window) == .alertFirstButtonReturn {
                    try await Updater.install(release)
                }
            } catch {
                if !automatic { showMessage("Check for Updates", error.localizedDescription) }
            }
        }
    }
}

if CommandLine.arguments.dropFirst().first == "--self-test" {
    do { try selfTest(); print("StreamSift Mac checks passed.") }
    catch { fputs("Mac self-test failed: \(error)\n", stderr); exit(1) }
} else {
    signal(SIGPIPE, SIG_IGN)
    MainActor.assumeIsolated {
        let application = NSApplication.shared
        application.setActivationPolicy(.regular)
        let app = StreamSiftApp()
        application.delegate = app
        application.run()
    }
}
