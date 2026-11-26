import AppKit
import Darwin
import UniformTypeIdentifiers

@MainActor final class StreamSiftApp: NSObject, NSApplicationDelegate, NSWindowDelegate, NSComboBoxDelegate,
                                     NSTableViewDataSource, NSTableViewDelegate, NSMenuItemValidation, NSToolbarDelegate,
                                     NSToolbarItemValidation {
    private let projectURL = URL(string: "https://github.com/OnjLouis/StreamSift")!
    private let window = KeyboardWindow(contentRect: NSRect(x: 0, y: 0, width: 730, height: 505),
                                        styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                        backing: .buffered, defer: false)
    private let address = NSComboBox()
    private let find = NSButton(title: "Find Streams and Files", target: nil, action: nil)
    private let findDirectory = NSButton(title: "Look Up in Radio Directory", target: nil, action: nil)
    private let cancel = NSButton(title: "Cancel", target: nil, action: nil)
    private let table = StreamTable()
    private let actionsToolbar = NSToolbar(identifier: "StreamSift.Actions")
    private let toolbarVisibilityKey = "ShowToolbar"
    private let details = DetailTable()
    private let status = readOnlyText("Status", hint: "Read the latest search or action result.")
    private let defaults = UserDefaults.standard
    private lazy var history = SearchHistory(reading: defaults)
    private var historyOpen = false
    private var settings: PreferencesWindow?
    private weak var reviewPanel: HelpPanel?
    private var preferences = Preferences()
    private var startSound: NSSound?
    private var searchPulseTimer: Timer?
    private var doneSound: NSSound?
    private var failureSound: NSSound?
    private var stations: [Station] = []
    private var allStations: [Station] = []
    private var fileIndexResults = false
    private var resultView = ResultView()
    private var resultQuery = ""
    private lazy var favourites = Favourites.read(from: defaults)
    private var showingFavourites = false
    private var favouriteView = ResultView()
    private var searchSelection = Set<String>()
    private var favouriteSelection = Set<String>()
    private var searchStatus = "Ready."
    private var searchResultQuery = ""
    private var favouriteResultQuery = ""
    private var lastPlayed: String?
    private var transferringFavourites = false
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
        failureSound = SearchCompletionCue.failureData.flatMap { NSSound(data: $0) }
        buildMenus()
        buildWindow()
        let restored = restoreLastResults()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(restored ? table : address)
        if preferences.startupUpdates { checkForUpdates(automatic: true) }
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
    func applicationWillTerminate(_ notification: Notification) {
        stopSearchPulse()
        rememberLastResults()
        job?.cancel()
    }
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
            self?.escapeMainWindow() ?? false
        }
        let caption = NSTextField(labelWithString: "Web address or station name:")
        caption.setAccessibilityServesAsTitleForUIElements([address])
        address.isBezeled = true
        address.target = self
        address.action = #selector(findStreams)
        address.delegate = self
        address.numberOfVisibleItems = SearchHistory.capacity
        address.completes = false
        refreshHistory()
        address.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        label("Web address or station name", for: address,
              hint: "Enter a public website, stream, file or folder address, station name or call sign, or choose a recent search.")
        find.target = self
        find.action = #selector(findStreams)
        findDirectory.target = self
        findDirectory.action = #selector(findInDirectory)
        cancel.target = self
        cancel.action = #selector(cancelSearch)
        label("Find Streams and Files", for: find, hint: "Search a public address for streams and files, or find a radio station by name or call sign.")
        label("Look Up in Radio Directory", for: findDirectory,
              hint: "Look up a station name, call sign or website in public radio directories and verify matching streams.")
        label("Cancel", for: cancel, hint: "Stop the current search. You can also press Escape.")
        let entry = NSStackView(views: [caption, address])
        entry.alignment = .centerY
        let searchButtons = NSStackView(views: [find, findDirectory, cancel])
        searchButtons.alignment = .centerY

        label("Results", for: table, hint: "Command+A selects all visible results. Return plays one result or the selected tracks. F4 reviews the current line. Command+3 moves to Details.")
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = true
        table.allowsEmptySelection = false
        table.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        let streamColumn = NSTableColumn(identifier: NSUserInterfaceItemIdentifier(FileResultColumn.name.rawValue))
        streamColumn.title = "Stream"
        streamColumn.width = FileResultColumn.name.width
        streamColumn.minWidth = 140
        streamColumn.resizingMask = [.autoresizingMask, .userResizingMask]
        table.addTableColumn(streamColumn)
        table.target = self
        table.doubleAction = #selector(previewStream)
        table.copyAddress = { [weak self] in self?.copyAddress() }
        table.openPlayer = { [weak self] in self?.openDefaultPlayer() }
        table.previewStream = { [weak self] in self?.previewStream() }
        table.openBrowser = { [weak self] in self?.openStreamInBrowser() }
        table.reviewLine = { [weak self] in self?.reviewStreamLine() }
        table.didSelectAll = { [weak self] in
            guard let self else { return }
            let count = table.selectedRowIndexes.count
            let kind = tracksSelected ? "track" : "stream"
            setStatus("\(count) \(kind)\(count == 1 ? "" : "s") selected.")
        }
        table.actionMenu = { [weak self] in self?.resultMenu() }
        table.dataSource = self
        table.delegate = self
        details.configure()
        label("Details", for: details,
              hint: "F4 reviews the current line. Return searches the address or opens Save Playlist on an HLS address. On an address row, Shift+Return scans the server where available.")
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
        actionsToolbar.delegate = self
        actionsToolbar.displayMode = .iconAndLabel
        window.toolbarStyle = .unifiedCompact
        window.toolbar = actionsToolbar
        actionsToolbar.isVisible = defaults.object(forKey: toolbarVisibilityKey) == nil || defaults.bool(forKey: toolbarVisibilityKey)
        for view in [entry, listView, detailView, statusView] {
            view.widthAnchor.constraint(equalTo: content.widthAnchor, constant: -28).isActive = true
        }
        searchButtons.widthAnchor.constraint(lessThanOrEqualTo: content.widthAnchor, constant: -28).isActive = true
        let preferredDetailHeight = detailView.heightAnchor.constraint(equalToConstant: 118)
        preferredDetailHeight.priority = .defaultLow
        NSLayoutConstraint.activate([
            listView.heightAnchor.constraint(greaterThanOrEqualToConstant: 112),
            detailView.heightAnchor.constraint(greaterThanOrEqualToConstant: 60),
            preferredDetailHeight
        ])
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
            item("Find Streams and Files", #selector(findStreams), "\r"),
            item("Look Up in Radio Directory", #selector(findInDirectory), "d", modifiers: [.command, .shift]),
            item("Find with Browser", #selector(findWithBrowser), "\r", modifiers: [.command, .shift]),
            item("Refresh Results", #selector(refreshResults), "r"), .separator(),
            item("Copy Addresses", #selector(copyAddress)),
            item("Play Result", #selector(previewStream)),
            item("Play Random", #selector(playRandom), "r", modifiers: [.command, .shift]),
            item("Open in Browser", #selector(openFocusedAddressInBrowser), "\r", modifiers: .option),
            item("Save and Open in Default Player", #selector(openDefaultPlayer)), .separator(),
            item("Go to Address", #selector(focusAddress), "1"),
            item("Go to Results", #selector(focusStreams), "2"),
            item("Go to Details", #selector(focusDetails), "3"),
            item("Go to Status", #selector(focusStatus), "4"), .separator(),
            item("Filter and Sort...", #selector(showResultFilter), "l"),
            item("Find in Results...", #selector(showResultFind), "f"),
            item("Find Next", #selector(findNextResult), "g"),
            item("Find Previous", #selector(findPreviousResult), "g", modifiers: [.command, .shift])
        ])
        _ = section("Favourites", [
            item("Show Favourites", #selector(toggleFavourites), "b", modifiers: [.command, .shift]),
            item("Add to Favourites", #selector(toggleFavourite), "d"),
            .separator(),
            item("Import Favourites...", #selector(importFavourites), "i", modifiers: [.command, .shift]),
            item("Export Favourites...", #selector(exportFavourites), "e", modifiers: [.command, .shift])
        ])
        _ = section("View", [item("Show Toolbar", #selector(toggleToolbar))])
        NSApp.windowsMenu = section("Window", [
            NSMenuItem(title: "Minimize", action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m"),
            NSMenuItem(title: "Zoom", action: #selector(NSWindow.performZoom(_:)), keyEquivalent: "")
        ])
        NSApp.helpMenu = section("Help", [
            item("Context Help", #selector(focusedHelp), functionOne, modifiers: []),
            item("Open Manual", #selector(openManual)),
            item("Version History…", #selector(openVersionHistory)),
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
    @objc private func focusStatus() { window.makeFirstResponder(status) }

    private var tracksSelected: Bool {
        let selection = selectedStations
        return !selection.isEmpty && selection.allSatisfy { $0.isTrack == true }
    }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        if let command = ToolbarAction.allCases.first(where: { $0.selector == menuItem.action }) {
            let state = toolbarState(command)
            menuItem.title = state.title
            return state.enabled
        }
        switch menuItem.action {
        case #selector(toggleToolbar):
            menuItem.state = actionsToolbar.isVisible ? .on : .off
            return window.isKeyWindow && window.attachedSheet == nil
        case #selector(savePlaylist), #selector(copyAddress), #selector(openDefaultPlayer), #selector(focusDetails):
            return selected != nil && window.isKeyWindow && window.attachedSheet == nil
        case #selector(openFocusedAddressInBrowser):
            return (window.focusedView === details ? details.selectedDetail?.address != nil : table.selectedRowIndexes.count == 1) &&
                window.isKeyWindow && window.attachedSheet == nil
        case #selector(openStreamInBrowser):
            return table.selectedRowIndexes.count == 1 && window.isKeyWindow && window.attachedSheet == nil
        case #selector(activateDetail):
            return details.canSearch && details.selectedDetail?.enterCommand != nil && window.attachedSheet == nil
        case #selector(scanDetailServer):
            return details.canSearch && details.selectedDetail?.server != nil && window.attachedSheet == nil
        case #selector(reviewStreamLine):
            return stations.indices.contains(table.selectedRow) && window.attachedSheet == nil
        case #selector(openDetail), #selector(copyDetail), #selector(reviewDetailLine):
            return details.selectedDetail != nil && window.attachedSheet == nil
        case #selector(focusAddress):
            return job == nil && window.isKeyWindow && window.attachedSheet == nil
        case #selector(focusStatus):
            return window.isKeyWindow && window.attachedSheet == nil
        case #selector(focusStreams):
            menuItem.title = showingFavourites ? "Go to Favourites" : "Go to Results"
            return !stations.isEmpty && window.isKeyWindow && window.attachedSheet == nil
        case #selector(showResultFind), #selector(findNextResult), #selector(findPreviousResult):
            return job == nil && !stations.isEmpty && window.isKeyWindow && window.attachedSheet == nil
        case #selector(refreshResults):
            return !showingFavourites && job == nil && lastSearch != nil && !historyOpen && window.isKeyWindow && window.attachedSheet == nil
        case #selector(importFavourites):
            return favouritesCommandsAvailable && !transferringFavourites
        case #selector(exportFavourites):
            return favouritesCommandsAvailable && !transferringFavourites && !favourites.isEmpty
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

    private func streamLine(at row: Int) -> String? {
        guard stations.indices.contains(row) else { return nil }
        if visibleFileColumns && stations[row].isTrack == true {
            return FileResultColumn.reviewLine(for: stations[row], named: names[row])
        }
        return stations[row].listLabel(named: names[row])
    }

    func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
        guard stations.indices.contains(row) else { return nil }
        let column = tableColumn.flatMap { FileResultColumn(rawValue: $0.identifier.rawValue) }
        let text = visibleFileColumns ? (column ?? .name).value(for: stations[row], named: names[row])
            : stations[row].listLabel(named: names[row])
        let cell = NSTextField(labelWithString: text)
        cell.lineBreakMode = .byTruncatingMiddle
        cell.toolTip = text
        if visibleFileColumns && column == .size { cell.alignment = .right }
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
        findDirectory.isEnabled = job == nil
        cancel.isEnabled = job != nil
        actionsToolbar.validateVisibleItems()
        let controls = mainKeyOrder
        for (index, view) in controls.enumerated() { view.nextKeyView = controls[(index + 1) % controls.count] }
    }

    private var mainKeyOrder: [NSView] {
        (job == nil ? [address, find, findDirectory] as [NSView] : [cancel]) + (stations.isEmpty ? [] : [table]) +
            (selected == nil ? [] : [details]) + [status]
    }

    private func restoreLastResults() -> Bool {
        guard let saved = LastResults.read(from: defaults) else { return false }
        lastSearch = (saved.input, saved.mode)
        address.stringValue = saved.input
        allStations = saved.stations
        resultView = saved.view
        render(resultView.apply(allStations), preserving: Set(saved.selection.map { [$0] } ?? []))
        setStatus("Restored \(stations.count) \(stations.count == 1 ? "stream" : "streams") from a saved search. Press Command+R to refresh.")
        return true
    }

    private func rememberLastResults() {
        guard !allStations.isEmpty, let lastSearch else { return }
        let row = table.selectedRow
        let selection = showingFavourites ? searchSelection.first : stations.indices.contains(row) ? stations[row].url : nil
        LastResults.capture(input: lastSearch.input, mode: lastSearch.mode, stations: allStations,
                            view: resultView, selection: selection)?
            .save(to: defaults)
    }

    private func load(_ found: [Station], fileIndex: Bool = false, preferListedOrder: Bool = false) {
        allStations = found
        fileIndexResults = fileIndex
        resultView = ResultView.forSearch(fileIndex: fileIndex, preferListedOrder: preferListedOrder)
        resultQuery = ""
        render(resultView.apply(found), preserving: [])
    }

    private func render(_ found: [Station], preserving selection: Set<String>, switching: Bool = false) {
        guard switching || stations != found || table.showsFileColumns != visibleFileColumns else { refreshNavigation(); return }
        stations = found
        func group(_ row: Station) -> [String] { [row.name, row.format, String(row.bitrate ?? -1)] }
        let counts = Dictionary(grouping: found, by: group)
        names = found.map { row in
            guard counts[group(row), default: []].count > 1, let url = URL(string: row.url) else { return row.name }
            return "\(row.name) (\(url.host ?? "")\(url.path))"
        }
        table.setFileColumns(visibleFileColumns)
        table.reloadData()
        if !found.isEmpty {
            let kept = IndexSet(found.indices.filter { selection.contains(found[$0].url) })
            table.selectRowIndexes(kept.isEmpty ? IndexSet(integer: 0) : kept, byExtendingSelection: false)
            table.scrollRowToVisible(kept.first ?? 0)
        }
        tableViewSelectionDidChange(Notification(name: NSTableView.selectionDidChangeNotification))
    }

    @objc private func showResultFilter() {
        let source = activeResults
        let view = activeView
        guard !source.isEmpty, window.attachedSheet == nil else { return }
        let formats = ResultView.formats(source)
        let format = NSPopUpButton()
        format.addItems(withTitles: ["All formats"] + formats)
        format.selectItem(at: view.format.flatMap { formats.firstIndex(of: $0) }.map { $0 + 1 } ?? 0)
        let sort = NSPopUpButton()
        let tracks = source.contains { $0.isTrack == true }
        let orders = ResultView.availableOrders(source)
        sort.addItems(withTitles: orders.map { $0.label(tracks: tracks) })
        sort.selectItem(at: orders.firstIndex(of: view.order) ?? 0)
        let sheet = ResultCommandSheet(title: "Filter and Sort", action: "Apply", fields: [
            ("Format", format, "Show all formats or a format found in the current search."),
            ("Sort by", sort, tracks ? "Choose listed order, track name, date modified or file size where listed. Missing values appear last."
                : "Choose search order, station name, bitrate or listeners where reported. Unknown counts and bitrates appear last.")
        ], manual: { [weak self] in self?.openManual() })
        sheet.present(on: window) { [weak self] in
            guard let self else { return }
            let index = format.indexOfSelectedItem - 1
            let sortIndex = sort.indexOfSelectedItem
            let changed = ResultView(format: formats.indices.contains(index) ? formats[index] : nil,
                                    order: orders.indices.contains(sortIndex) ? orders[sortIndex] : .search)
            if showingFavourites { favouriteView = changed } else { resultView = changed }
            render(changed.apply(activeResults), preserving: Set(selectedStations.map(\.url)))
            setStatus("Showing \(stations.count) of \(activeResults.count) \(showingFavourites ? "favourites" : tracks ? "tracks" : "streams").")
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
            ("Find what", input, "Find visible results by name, format, address, bitrate or listener count where available.")
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
        guard let lastSearch, !historyOpen, !showingFavourites else { return }
        startSearch(mode: lastSearch.mode, refreshInput: lastSearch.input)
    }

    private func startSearch(mode: SearchMode, refreshInput: String? = nil, newInput: String? = nil) {
        guard job == nil else { return }
        let refreshing = refreshInput != nil
        let input = refreshInput ?? newInput ?? address.currentEditor()?.string ?? address.stringValue
        let running = ScanJob()
        rememberLastResults()
        do {
            try running.start(input, mode: mode, fallback: preferences.automaticFallback, recentSearches: history.entries,
                              onFallback: { [weak self, weak running] next in
                guard let self, job === running, !isCancelling else { return }
                setStatus("Trying another method. " + next.starting)
            }) { [weak self] result in self?.finished(result, mode: mode, input: input, refreshing: refreshing) }
            if showingFavourites { toggleFavourites() }
            isCancelling = false
            job = running
            lastSearch = (input, mode)
            if !refreshing { load([]) }
            refreshNavigation()
            window.makeFirstResponder(cancel)
            if refreshing || newInput != nil { address.stringValue = input }
            setStatus(refreshing ? "Refreshing results..." : mode.starting)
            if preferences.sounds, startSound != nil {
                playSearchPulse()
                searchPulseTimer = Timer.scheduledTimer(timeInterval: 2, target: self,
                    selector: #selector(playSearchPulse), userInfo: nil, repeats: true)
            }
        } catch {
            setStatus(error.localizedDescription)
            window.makeFirstResponder(address)
        }
    }

    @objc private func cancelSearch() {
        guard let job, !isCancelling else { return }
        isCancelling = true
        stopSearchPulse()
        job.cancel()
    }

    private func escapeMainWindow() -> Bool {
        guard !historyOpen, window.attachedSheet == nil else { return false }
        if job != nil { cancelSearch(); return true }
        guard showingFavourites else { return false }
        toggleFavourites()
        return true
    }

    @objc private func playSearchPulse() {
        guard preferences.sounds, job != nil, !isCancelling else { return }
        startSound?.stop()
        startSound?.play()
    }

    private func stopSearchPulse() {
        searchPulseTimer?.invalidate()
        searchPulseTimer = nil
        startSound?.stop()
    }

    private func finished(_ result: Result<SearchResult, Error>, mode: SearchMode, input: String, refreshing: Bool) {
        stopSearchPulse()
        let focused = window.focusedView
        let moveFocus = focused === cancel || focused === address || focused === find || focused === findDirectory
        job = nil
        var message: String
        if isCancelling {
            message = refreshing && !stations.isEmpty ? "Refresh cancelled. Previous results kept." : "Search cancelled."
        } else {
            switch result {
            case .success(let found):
                let rememberable = found.entryWasWebsite == true ? found.stations.count :
                    found.stations.filter { $0.isVideo != true && $0.isTrack != true }.count
                if history.remember(input, verifiedStreams: rememberable, in: defaults) { refreshHistory() }
                let previousFormat = resultView.format
                if refreshing {
                    allStations = found.stations
                    let sameResultType = fileIndexResults == (found.entryWasFileIndex == true)
                    fileIndexResults = found.entryWasFileIndex == true
                    resultView = sameResultType ? resultView.retainingAvailable(in: allStations, fileIndex: fileIndexResults)
                        : ResultView.forSearch(fileIndex: fileIndexResults, preferListedOrder: found.preferListedOrder == true)
                    render(resultView.apply(allStations), preserving: Set(selectedStations.map(\.url)))
                } else {
                    load(found.stations, fileIndex: found.entryWasFileIndex == true, preferListedOrder: found.preferListedOrder == true)
                }
                rememberLastResults()
                let prefix = found.stations.isEmpty && (found.attempts ?? 0) > 1 ? "Automatic search: " : (found.method ?? mode).resultPrefix
                message = (refreshing ? "Refresh finished. " : "") + prefix + found.summary
                if refreshing, let format = resultView.format {
                    let kind = found.entryWasFileIndex == true ? "tracks" : "streams"
                    message += " Showing \(stations.count) \(format) \(kind)."
                } else if refreshing, let previousFormat, !allStations.isEmpty {
                    message += " \(previousFormat) is no longer available. Showing all formats."
                }
            case .failure(let error):
                message = refreshing && !stations.isEmpty ? "Refresh could not finish. Previous results kept." :
                    ((error as? StreamError)?.errorDescription ?? StreamError.search.errorDescription ?? "Search failed.")
            }
        }
        refreshNavigation()
        let notice = deferredOffer ? " An update is available. Press Shift+F1 to review it." : ""
        deferredOffer = false
        setStatus(message + notice)
        if let cue = SearchCompletionCue.select(verifiedStreams: (try? result.get().stations.count) ?? 0,
                                                enabled: preferences.sounds, cancelled: isCancelling) {
            let sound = cue == .success ? doneSound : failureSound
            sound?.stop()
            sound?.play()
        }
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
        if showingFavourites { toggleFavourites() }
        LastResults.clear(in: defaults)
        lastSearch = nil
        if address.indexOfSelectedItem >= 0 { address.deselectItem(at: address.indexOfSelectedItem) }
        address.stringValue = ""
        load([])
        setStatus("Ready.")
        window.makeFirstResponder(address)
    }

    private func resultMenu() -> NSMenu {
        let menu = NSMenu()
        menu.addItem(item("Play Result", #selector(previewStream), "\r", modifiers: []))
        menu.addItem(item("Open Address in Browser", #selector(openStreamInBrowser), "\r", modifiers: .option))
        menu.addItem(item(table.selectedRowIndexes.count == 1 ? "Copy Address" : "Copy Addresses", #selector(copyAddress), "c"))
        menu.addItem(item("Review Text...", #selector(reviewStreamLine), functionFour, modifiers: []))
        menu.addItem(item("Save Playlist...", #selector(savePlaylist), "s"))
        menu.addItem(item("Save and Open in Default Player", #selector(openDefaultPlayer), "\r", modifiers: .shift))
        if table.selectedRowIndexes.count == 1 {
            menu.addItem(item(favouriteActionTitle, #selector(toggleFavourite), "d"))
        }
        return menu
    }

    private func detailMenu() -> NSMenu? {
        guard let row = details.selectedDetail else { return nil }
        let menu = NSMenu()
        if let command = row.enterCommand {
            let title: String
            switch command {
            case .savePlaylist: title = "Save Playlist..."
            case .scanServer: title = "Scan Stream Server"
            default: title = "Search This Address"
            }
            menu.addItem(item(title, #selector(activateDetail), "\r", modifiers: []))
        }
        if row.address != nil {
            menu.addItem(item("Open in Browser", #selector(openDetail), "\r", modifiers: .option))
        }
        menu.addItem(item(row.address == nil ? "Copy Value" : "Copy Address", #selector(copyDetail), "c"))
        menu.addItem(item("Review Text...", #selector(reviewDetailLine), functionFour, modifiers: []))
        if row.kind == .streamAddress, row.server != nil {
            menu.addItem(item("Scan Stream Server", #selector(scanDetailServer), "\r", modifiers: .shift))
        }
        return menu
    }

    @objc private func activateDetail() {
        if let row = details.selectedDetail, let command = row.enterCommand { runDetailAction(command, row: row) }
    }

    @objc private func openDetail() {
        if let row = details.selectedDetail { runDetailAction(.openBrowser, row: row) }
    }

    @objc private func copyDetail() {
        if let row = details.selectedDetail { runDetailAction(.copy, row: row) }
    }

    @objc private func reviewStreamLine() {
        if let text = streamLine(at: table.selectedRow) { reviewLine(text) }
    }

    @objc private func reviewDetailLine() {
        if let row = details.selectedDetail { runDetailAction(.review, row: row) }
    }

    private func reviewLine(_ text: String) {
        guard window.attachedSheet == nil else { return }
        let review = HelpPanel(title: "Review Text", text: text, bodyLabel: "Text")
        review.enableReviewHelp { [weak self] in self?.openManual() }
        reviewPanel = review
        review.present(on: window)
    }

    @objc private func scanDetailServer() {
        if let row = details.selectedDetail { runDetailAction(.scanServer, row: row) }
    }

    private func runDetailAction(_ command: DetailTable.Command, row: DetailRow) {
        switch command {
        case .search:
            if details.canSearch, let target = row.address { startSearch(mode: .passive, newInput: target.absoluteString) }
        case .scanServer:
            if details.canSearch, let server = row.server { startSearch(mode: .passive, newInput: server.absoluteString) }
        case .savePlaylist:
            if details.canSearch, let station = row.playlistStation { exportPlaylist([station]) }
        case .openBrowser:
            if let target = row.address { openAddressInBrowser(target) }
        case .copy:
            copyText(row.value, confirmation: row.label + " copied.")
        case .review:
            reviewLine(row.text)
        }
    }

    @objc private func copyAddress() {
        let selection = selectedStations
        guard !selection.isEmpty else { return }
        copyText(selection.map(\.url).joined(separator: "\n"),
                 confirmation: selection.count == 1 ? "Address copied." : "\(selection.count) addresses copied.")
    }

    private func copyText(_ text: String, confirmation: String) {
        NSPasteboard.general.clearContents()
        if NSPasteboard.general.setString(text, forType: .string) { setStatus(confirmation) }
        else { showMessage("Copy Could Not Finish", "The clipboard is unavailable. Try again.") }
    }

    @objc private func savePlaylist() {
        exportPlaylist(selectedStations)
    }

    private func exportPlaylist(_ selection: [Station]) {
        guard !selection.isEmpty, window.attachedSheet == nil else { return }
        do { try FileManager.default.createDirectory(at: preferences.folder, withIntermediateDirectories: true) }
        catch { showMessage("Could Not Open Playlist Folder", error.localizedDescription); return }
        let save = NSSavePanel()
        save.title = "Save Playlist"
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

    @objc private func previewStream() { play(selectedStations) }

    private func play(_ selection: [Station], random: Bool = false) {
        guard let first = selection.first, window.attachedSheet == nil else { return }
        if selection.count > 1 && !selection.allSatisfy({ $0.isTrack == true }) {
            setStatus("Select one result to play it, or press Shift+Return to save and open the selection.")
            return
        }
        guard let address = StreamPreview.address(first) else {
            showMessage("Could Not Play Result", "The result does not have a public HTTP or HTTPS address.")
            return
        }
        let workspace = NSWorkspace.shared
        let associated = UTType(filenameExtension: "m3u8").flatMap { workspace.urlForApplication(toOpen: $0) }
        guard let target = StreamPreview.target(choice: preferences.mediaPlayer, customPath: preferences.customPlayerPath, associated: associated,
            associatedIdentifier: associated.flatMap { Bundle(url: $0)?.bundleIdentifier },
            installed: { workspace.urlForApplication(withBundleIdentifier: $0) }) else {
            let message: String
            switch preferences.mediaPlayer {
            case .automatic: message = "No playlist player was found. Use Choose Player in Settings or set a default app for .m3u8 files."
            case .custom: message = "The custom player could not be found. Use Choose Player in Settings."
            default: message = "StreamSift could not find \(preferences.mediaPlayer.title). Use Choose Player in Settings to locate it, or select Automatic."
            }
            showMessage("Play Result", message)
            return
        }
        let playback: URL
        let usesPlaylist = target.needsPlaylist || selection.count > 1
        do { playback = try usesPlaylist ? PlaybackHandoff.stage(selection) : address }
        catch { showMessage("Could Not Play Result", error.localizedDescription); return }
        // An explicit app target keeps an HTTP address out of the browser.
        workspace.open([playback], withApplicationAt: target.app, configuration: NSWorkspace.OpenConfiguration()) { [weak self] app, error in
            DispatchQueue.main.async {
                if error != nil || app == nil {
                    if usesPlaylist { try? FileManager.default.removeItem(at: playback) }
                    self?.showMessage("Could Not Play Result", "The player could not open the selection. Choose another player in Settings.")
                } else {
                    let player = target.player?.displayName(customPath: target.app.path) ?? "your default playlist player"
                    if selection.count == 1 { self?.lastPlayed = first.url }
                    self?.setStatus(random ? "Sent \(first.name) to \(player)." :
                        selection.count == 1 ? "Sent to \(player)." : "Sent \(selection.count) tracks to \(player).")
                    if usesPlaylist {
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
            showMessage("Could Not Open Address", "The address could not be opened in your browser.")
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
        setStatus("Playlist saved: \(file.lastPathComponent)" + (count > 1 ? ". \(count) items." : ""))
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
    @objc private func openVersionHistory() { NSWorkspace.shared.open(projectURL.appendingPathComponent("releases")) }
    @objc private func donate() { NSWorkspace.shared.open(URL(string: "https://onj.me/donate")!) }
    @objc private func about() { NSApp.orderFrontStandardAboutPanel(nil) }
    @objc private func openManual() {
        if let manual = Bundle.main.url(forResource: "Manual", withExtension: "html") { NSWorkspace.shared.open(manual) }
    }

    @objc private func focusedHelp() {
        if settings?.window.isKeyWindow == true { settings?.showHelp(); return }
        guard window.attachedSheet == nil else { reviewPanel?.showReviewHelp(); return }
        let focus = window.focusedView
        let help: (String, String)
        if focus === details, let row = details.selectedDetail {
            help = (row.label + " Help", ContextGuide.detail(row, canSearch: details.canSearch))
        } else if focus === table {
            help = ("Results Help", ContextGuide.streams(selected: table.selectedRowIndexes.count, tracks: tracksSelected,
                                                        fileColumns: visibleFileColumns) +
                "\nCommand+D adds or removes a favourite. Command+Shift+B switches views. Command+Shift+R plays a random visible result." +
                (showingFavourites ? "\nEscape returns to search results." : ""))
        } else {
            let text = focus?.accessibilityHelp().flatMap { $0.isEmpty ? nil : $0 } ??
                "Enter a website address and press Return. When streams are found, use the results table to copy or save a stream."
            help = (focus?.accessibilityLabel() ?? "StreamSift Help", text)
        }
        HelpPanel(title: help.0, text: help.1, manual: { [weak self] in self?.openManual() })
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

extension StreamSiftApp {
    private enum ToolbarAction: String, CaseIterable {
        case play, random, filter, favourites, saveFavourite

        var identifier: NSToolbarItem.Identifier { NSToolbarItem.Identifier(rawValue) }

        var selector: Selector {
            switch self {
            case .play: return #selector(StreamSiftApp.previewStream)
            case .random: return #selector(StreamSiftApp.playRandom)
            case .filter: return #selector(StreamSiftApp.showResultFilter)
            case .favourites: return #selector(StreamSiftApp.toggleFavourites)
            case .saveFavourite: return #selector(StreamSiftApp.toggleFavourite)
            }
        }

        var shortcut: String {
            switch self {
            case .play: return "Return"
            case .random: return "Command+Shift+R"
            case .filter: return "Command+L"
            case .favourites: return "Command+Shift+B"
            case .saveFavourite: return "Command+D"
            }
        }

        var symbol: String {
            switch self {
            case .play: return "play.fill"
            case .random: return "shuffle"
            case .filter: return "line.3.horizontal.decrease.circle"
            case .favourites: return "star"
            case .saveFavourite: return "plus.circle"
            }
        }
    }

    private func toolbarState(_ action: ToolbarAction) -> (title: String, enabled: Bool) {
        let single = table.selectedRowIndexes.count == 1
        let available = window.isKeyWindow && window.attachedSheet == nil
        switch action {
        case .play:
            let tracks = tracksSelected
            return (tracks ? (single ? "Play Track" : "Play Selected Tracks") : "Play Result", available && (single || tracks))
        case .random:
            return ("Play Random", favouritesCommandsAvailable && !stations.isEmpty)
        case .filter:
            return ("Filter and Sort...", available && job == nil && !activeResults.isEmpty)
        case .favourites:
            return (showingFavourites ? "Return to Search Results" : "Show Favourites", favouritesCommandsAvailable)
        case .saveFavourite:
            return (favouriteActionTitle, favouritesCommandsAvailable && single)
        }
    }

    @objc private func toggleToolbar() {
        actionsToolbar.isVisible.toggle()
        defaults.set(actionsToolbar.isVisible, forKey: toolbarVisibilityKey)
        actionsToolbar.validateVisibleItems()
    }

    func toolbarAllowedItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        ToolbarAction.allCases.map(\.identifier) + [.space]
    }

    func toolbarDefaultItemIdentifiers(_ toolbar: NSToolbar) -> [NSToolbarItem.Identifier] {
        [ToolbarAction.play.identifier, ToolbarAction.random.identifier, ToolbarAction.filter.identifier,
         .space, ToolbarAction.favourites.identifier, ToolbarAction.saveFavourite.identifier]
    }

    func toolbar(_ toolbar: NSToolbar, itemForItemIdentifier identifier: NSToolbarItem.Identifier,
                 willBeInsertedIntoToolbar willBeInserted: Bool) -> NSToolbarItem? {
        guard let action = ToolbarAction(rawValue: identifier.rawValue) else { return nil }
        let item = NSToolbarItem(itemIdentifier: identifier)
        item.image = NSImage(systemSymbolName: action.symbol, accessibilityDescription: nil)
        item.target = self
        item.action = action.selector
        item.isEnabled = validateToolbarItem(item)
        return item
    }

    func validateToolbarItem(_ item: NSToolbarItem) -> Bool {
        guard let action = ToolbarAction(rawValue: item.itemIdentifier.rawValue) else { return false }
        let state = toolbarState(action)
        if action == .saveFavourite && item.label != state.title {
            item.image = NSImage(systemSymbolName: selectedIsFavourite ? "minus.circle" : action.symbol,
                                 accessibilityDescription: nil)
        }
        item.label = state.title
        item.toolTip = "\(state.title) (\(action.shortcut))"
        return state.enabled
    }
}

extension StreamSiftApp {
    private var activeResults: [Station] { showingFavourites ? favourites : allStations }
    private var activeView: ResultView { showingFavourites ? favouriteView : resultView }
    private var visibleFileColumns: Bool {
        showingFavourites ? favourites.contains { $0.isTrack == true } : fileIndexResults
    }
    private var favouritesCommandsAvailable: Bool {
        job == nil && !historyOpen && window.isKeyWindow && window.attachedSheet == nil
    }
    private var selectedIsFavourite: Bool {
        selected.map { row in favourites.contains { $0.url == row.url } } == true
    }

    private var favouriteActionTitle: String { selectedIsFavourite ? "Remove from Favourites" : "Add to Favourites" }

    @objc private func toggleFavourites() {
        guard job == nil else { return }
        let selection = Set(selectedStations.map(\.url))
        if showingFavourites {
            favouriteSelection = selection
            favouriteResultQuery = resultQuery
        } else {
            searchSelection = selection
            searchResultQuery = resultQuery
            searchStatus = status.string
        }
        showingFavourites.toggle()
        resultQuery = showingFavourites ? favouriteResultQuery : searchResultQuery
        table.setAccessibilityLabel(showingFavourites ? "Favourites" : "Results")
        render(activeView.apply(activeResults), preserving: showingFavourites ? favouriteSelection : searchSelection, switching: true)
        let favouriteStatus = favourites.isEmpty ? "No favourites." : "Showing \(stations.count) of \(favourites.count) favourites."
        setStatus(showingFavourites ? favouriteStatus + " Press Escape to return to search results." : searchStatus)
        window.makeFirstResponder(stations.isEmpty ? status : table)
    }

    @objc private func toggleFavourite() {
        guard favouritesCommandsAvailable, table.selectedRowIndexes.count == 1, let row = selected else { return }
        guard Favourites.canSave(row) else {
            showMessage("Add to Favourites", "Choose a station or track with a stable public audio address.")
            return
        }
        let remove = favourites.contains { $0.url == row.url }
        let next = remove ? favourites.filter { $0.url != row.url } : Favourites.merge(favourites, with: [row])
        guard next != favourites else { showMessage("Favourites", FavouriteError.full.localizedDescription); return }
        do {
            try saveFavourites(next)
            setStatus(remove ? "Removed from Favourites." : "Added to Favourites.")
        } catch { showMessage("Favourites", error.localizedDescription) }
    }

    private func saveFavourites(_ next: [Station]) throws {
        try Favourites.save(next, to: defaults)
        favourites = next
        favouriteView = favouriteView.retainingAvailable(in: next)
        if showingFavourites {
            let hadFocus = window.focusedView === table || window.focusedView === details
            render(favouriteView.apply(next), preserving: Set(selectedStations.map(\.url)))
            if stations.isEmpty && hadFocus { window.makeFirstResponder(status) }
        } else { refreshNavigation() }
    }

    @objc private func playRandom() {
        guard favouritesCommandsAvailable, let row = Favourites.random(in: stations, avoiding: lastPlayed) else { return }
        play([row], random: true)
    }

    @objc private func importFavourites() {
        guard favouritesCommandsAvailable, !transferringFavourites else { return }
        let panel = NSOpenPanel()
        panel.title = "Import Favourites"
        panel.allowedContentTypes = ["m3u8", "m3u"].compactMap { UTType(filenameExtension: $0) }
        panel.beginSheetModal(for: window) { [weak self] response in
            guard let self, response == .OK, let url = panel.url else { return }
            transferringFavourites = true
            DispatchQueue.global(qos: .userInitiated).async {
                let converted = Result { try Favourites.importPlaylist(url) }
                DispatchQueue.main.async {
                    self.transferringFavourites = false
                    do {
                        let list = try converted.get()
                        let updated = Favourites.merge(self.favourites, with: list.stations)
                        let added = updated.count - self.favourites.count
                        if added > 0 { try self.saveFavourites(updated) }
                        self.setStatus(list.summary(added: added))
                    } catch { self.showMessage("Import Favourites", error.localizedDescription) }
                }
            }
        }
    }

    @objc private func exportFavourites() {
        guard favouritesCommandsAvailable, !transferringFavourites, !favourites.isEmpty else { return }
        let panel = NSSavePanel()
        panel.title = "Export Favourites"
        panel.nameFieldStringValue = "Favourites.m3u8"
        panel.allowedContentTypes = [UTType(filenameExtension: "m3u8")].compactMap { $0 }
        panel.beginSheetModal(for: window) { [weak self] response in
            guard let self, response == .OK, let url = panel.url else { return }
            transferringFavourites = true
            let entries = favourites
            DispatchQueue.global(qos: .userInitiated).async {
                let written = Result { try Favourites.exportPlaylist(entries, to: url) }
                DispatchQueue.main.async {
                    self.transferringFavourites = false
                    switch written {
                    case .success: self.setStatus("Favourites exported.")
                    case .failure(let error): self.showMessage("Export Favourites", error.localizedDescription)
                    }
                }
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
