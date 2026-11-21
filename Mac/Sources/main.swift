import AppKit
import Darwin
import UniformTypeIdentifiers

@MainActor final class StreamSiftApp: NSObject, NSApplicationDelegate, NSWindowDelegate, NSTextFieldDelegate,
                                     NSTableViewDataSource, NSTableViewDelegate, NSMenuItemValidation {
    private let projectURL = URL(string: "https://github.com/OnjLouis/StreamSift")!
    private let window = KeyboardWindow(contentRect: NSRect(x: 0, y: 0, width: 730, height: 505),
                                        styleMask: [.titled, .closable, .miniaturizable, .resizable],
                                        backing: .buffered, defer: false)
    private let address = NSTextField()
    private let find = NSButton(title: "Find Streams", target: nil, action: nil)
    private let findBrowser = NSButton(title: "Find with Browser", target: nil, action: nil)
    private let cancel = NSButton(title: "Cancel", target: nil, action: nil)
    private let table = StreamTable()
    private let details = readOnlyText("Stream details", hint: "Review the stream address and where it was found.")
    private let status = readOnlyText("Status", hint: "Read the latest search or action result.")
    private let defaults = UserDefaults.standard
    private var settings: PreferencesWindow?
    private var preferences = Preferences()
    private var startSound: NSSound?
    private var doneSound: NSSound?
    private var stations: [Station] = []
    private var names: [String] = []
    private var job: ScanJob?
    private var isCancelling = false
    private var checkingUpdate = false
    private var deferredOffer = false

    func applicationDidFinishLaunching(_ notification: Notification) {
        preferences = Preferences(reading: defaults)
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
        window.keyOrder = { [weak self] in self?.mainKeyOrder ?? [] }
        window.cancel = { [weak self] in
            guard self?.job != nil else { return false }
            self?.cancelSearch()
            return true
        }
        let caption = NSTextField(labelWithString: "Website address:")
        caption.setAccessibilityServesAsTitleForUIElements([address])
        address.isBezeled = true
        address.target = self
        address.action = #selector(findStreams)
        address.delegate = self
        address.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        label("Website address", for: address, hint: "Paste a public station page, playlist or stream address. Press Return to search.")
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

        label("Streams", for: table, hint: "Move through streams with the arrow keys. Return opens actions. Shift+Return opens the selected stream in your player.")
        table.usesAlternatingRowBackgroundColors = true
        table.allowsMultipleSelection = false
        table.allowsEmptySelection = false
        table.columnAutoresizingStyle = .firstColumnOnlyAutoresizingStyle
        let streamColumn = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("Stream"))
        streamColumn.title = "Stream"
        streamColumn.width = 630
        streamColumn.minWidth = 140
        streamColumn.resizingMask = [.autoresizingMask, .userResizingMask]
        table.addTableColumn(streamColumn)
        table.target = self
        table.doubleAction = #selector(openDefaultPlayer)
        table.copyAddress = { [weak self] in self?.copyAddress() }
        table.openPlayer = { [weak self] in self?.openDefaultPlayer() }
        table.actionMenu = { [weak self] in self?.resultMenu() }
        table.dataSource = self
        table.delegate = self
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
        detailView.heightAnchor.constraint(equalToConstant: 96).isActive = true
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
            item("Copy Stream Address", #selector(copyAddress)),
            item("Open in Default Player", #selector(openDefaultPlayer))
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

    private var selected: Station? { stations.indices.contains(table.selectedRow) ? stations[table.selectedRow] : nil }

    func validateMenuItem(_ menuItem: NSMenuItem) -> Bool {
        switch menuItem.action {
        case #selector(savePlaylist), #selector(copyAddress), #selector(openDefaultPlayer):
            return selected != nil && window.isKeyWindow && window.attachedSheet == nil
        case #selector(openPlaylistFolder):
            return window.isKeyWindow && window.attachedSheet == nil
        case #selector(reset), #selector(findStreams), #selector(findWithBrowser), #selector(findInDirectory):
            return job == nil && window.isKeyWindow && window.attachedSheet == nil
        case #selector(checkUpdates): return !checkingUpdate
        default: return true
        }
    }

    func control(_ control: NSControl, textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
        if commandSelector == #selector(NSResponder.insertNewline(_:)), control === address {
            findStreams()
            return true
        }
        return false
    }

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
        if let selected {
            details.string = [selected.name, "Format: " + selected.format,
                              selected.bitrate.map { "Bitrate: \($0) kbps" } ?? "Bitrate: not reported",
                              "Stream address:", selected.url, "Found on:", selected.foundOn].joined(separator: "\n")
        } else {
            details.string = ""
        }
        details.setSelectedRange(NSRange(location: 0, length: 0))
        refreshNavigation()
    }

    private func refreshNavigation() {
        table.hasStreams = !stations.isEmpty
        table.setAccessibilityElement(table.hasStreams)
        details.setAccessibilityElement(selected != nil)
        address.isEditable = job == nil
        find.isEnabled = job == nil
        findBrowser.isEnabled = job == nil
        cancel.isEnabled = job != nil
        let controls = mainKeyOrder
        for (index, view) in controls.enumerated() { view.nextKeyView = controls[(index + 1) % controls.count] }
    }

    private var mainKeyOrder: [NSView] {
        [address] + (job == nil ? [find, findBrowser] : [cancel]) + (stations.isEmpty ? [] : [table]) +
            (selected == nil ? [] : [details]) + [status]
    }

    private func load(_ found: [Station]) {
        stations = found
        func group(_ row: Station) -> [String] { [row.name, row.format, String(row.bitrate ?? -1)] }
        let counts = Dictionary(grouping: found, by: group)
        names = found.map { row in
            guard counts[group(row), default: []].count > 1, let url = URL(string: row.url) else { return row.name }
            return "\(row.name) (\(url.host ?? "")\(url.path))"
        }
        table.reloadData()
        if !found.isEmpty { table.selectRowIndexes(IndexSet(integer: 0), byExtendingSelection: false) }
        tableViewSelectionDidChange(Notification(name: NSTableView.selectionDidChangeNotification))
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

    @objc private func findStreams() { startSearch(mode: .passive) }

    @objc private func findWithBrowser() { startSearch(mode: .browser) }

    @objc private func findInDirectory() { startSearch(mode: .directory) }

    private func startSearch(mode: SearchMode) {
        guard job == nil else { return }
        let input = address.currentEditor()?.string ?? address.stringValue
        let running = ScanJob()
        do {
            try running.start(input, mode: mode) { [weak self] result in self?.finished(result, mode: mode) }
            isCancelling = false
            job = running
            load([])
            window.makeFirstResponder(cancel)
            setStatus(mode.starting)
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

    private func finished(_ result: Result<SearchResult, Error>, mode: SearchMode) {
        let focused = window.focusedView
        let moveFocus = focused === cancel || focused === address || focused === find || focused === findBrowser
        job = nil
        let message: String
        if isCancelling {
            message = "Search cancelled."
        } else {
            switch result {
            case .success(let found):
                load(found.stations)
                message = mode.resultPrefix + found.summary
                if preferences.sounds { doneSound?.stop(); doneSound?.play() }
            case .failure(let error):
                message = (error as? StreamError)?.errorDescription ?? StreamError.search.errorDescription ?? "Search failed."
            }
        }
        refreshNavigation()
        let notice = deferredOffer ? " An update is available. Press Shift+F1 to review it." : ""
        deferredOffer = false
        setStatus(message + notice)
        if moveFocus { window.makeFirstResponder(stations.isEmpty ? status : table) }
    }

    @objc private func reset() {
        guard job == nil else { return }
        address.stringValue = ""
        load([])
        setStatus("Ready.")
        window.makeFirstResponder(address)
    }

    private func resultMenu() -> NSMenu {
        let menu = NSMenu()
        menu.addItem(item("Copy Stream Address", #selector(copyAddress), "c"))
        menu.addItem(item("Save Playlist...", #selector(savePlaylist), "s"))
        menu.addItem(item("Open in Default Player", #selector(openDefaultPlayer), "\r", modifiers: .shift))
        return menu
    }

    @objc private func copyAddress() {
        guard let selected else { return }
        NSPasteboard.general.clearContents()
        if NSPasteboard.general.setString(selected.url, forType: .string) { setStatus("Stream address copied.") }
        else { showMessage("Copy Could Not Finish", "The clipboard is unavailable. Try again.") }
    }

    @objc private func savePlaylist() {
        guard let selected, window.attachedSheet == nil else { return }
        do { try FileManager.default.createDirectory(at: preferences.folder, withIntermediateDirectories: true) }
        catch { showMessage("Could Not Open Playlist Folder", error.localizedDescription); return }
        let save = NSSavePanel()
        save.title = "Save Stream Playlist"
        save.directoryURL = preferences.folder
        save.allowedContentTypes = [UTType(filenameExtension: "m3u8") ?? .data]
        save.nameFieldStringValue = Playlist.stem(selected.name) + ".m3u8"
        save.beginSheetModal(for: window) { [weak self] response in
            guard response == .OK, let file = save.url else { return }
            do {
                try Playlist.data(selected).write(to: file, options: .atomic)
                self?.setStatus("Playlist saved: \(file.lastPathComponent)")
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

    @objc private func openDefaultPlayer() {
        guard let selected, window.attachedSheet == nil else { return }
        do {
            let file = try Playlist.saveNew(selected, in: preferences.folder)
            setStatus("Playlist saved: \(file.lastPathComponent)")
            if !NSWorkspace.shared.open(file) {
                showMessage("Could Not Open the Playlist", "The playlist was saved at:\n\(file.path)\n\nChoose a default app for .m3u8 files in Finder.")
            }
        } catch { showMessage("Playlist Could Not Be Saved", error.localizedDescription) }
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
